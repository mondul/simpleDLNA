using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   End to end: a real HTTP server on a free port, a file server over a
  ///   scratch folder, and UPnP requests sent the way a TV sends them.
  /// </summary>
  [Collection(HttpServerCollection.NAME)]
  public sealed class HttpServerTests : IDisposable
  {
    private readonly TempDirectory media = new TempDirectory();

    private readonly DlnaTestServer server;

    public HttpServerTests(DlnaTestServer server)
    {
      this.server = server;
    }

    public void Dispose()
    {
      server.UnmountAll();
      media.Dispose();
    }

    private string Mount(DlnaMediaTypes types = DlnaMediaTypes.All)
    {
      return server.Mount(TestFileServer.Create(media.Info, types));
    }

    private static string RequireNonLoopbackAddress()
    {
      var address = DlnaTestServer.NonLoopbackAddress();
      Assert.SkipWhen(address == null, "this machine has no non-loopback IPv4 address");
      return address;
    }

    [Fact]
    public void BrowseListsFilesWithTheirUpnpClasses()
    {
      media.WriteFile("film.mkv");
      media.WriteFile("song.mp3");
      TestMedia.WriteJpeg(media.Combine("photo.jpg"), 64, 48);
      var prefix = Mount();

      var (status, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      Assert.Equal(HttpStatusCode.OK, status);
      Assert.Equal("object.item.videoItem.movie", items.Single(i => i.Title == "film").Class);
      Assert.Equal("object.item.audioItem.musicTrack", items.Single(i => i.Title == "song").Class);
      Assert.Equal("object.item.imageItem.photo", items.Single(i => i.Title == "photo").Class);
    }

    [Fact]
    public void MediaTypesLimitWhatIsListed()
    {
      media.WriteFile("film.mkv");
      media.WriteFile("song.mp3");
      var prefix = Mount(DlnaMediaTypes.Video);

      var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      Assert.Equal("film", Assert.Single(items).Title);
    }

    /// <summary>
    ///   Regression: StreamPump's completion callback used delegate
    ///   BeginInvoke, so every HTTP response failed when it finished.
    /// </summary>
    [Fact]
    public void FilesAreStreamedByteForByte()
    {
      var content = new byte[256 * 1024];
      new Random(7).NextBytes(content);
      File.WriteAllBytes(media.Combine("film.mkv"), content);
      var prefix = Mount();

      var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      Assert.Equal(content, DlnaClient.GetBytes(Assert.Single(items).Url));
    }

    /// <summary>
    ///   The MIME type reaches clients three ways, which must agree; and the
    ///   browse cache must not hand one client's types to another.
    /// </summary>
    [Fact]
    public void MatroskaAndWebMTypesDependOnTheClientEverywhere()
    {
      media.WriteFile("film.mkv");
      media.WriteFile("web.webm");
      media.WriteFile("clip.mp4");
      var prefix = Mount();

      void Expect(string userAgent, string mkv, string webm)
      {
        var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix, userAgent);
        foreach (var (title, expected) in new[] {("film", mkv), ("web", webm), ("clip", "video/mp4")}) {
          var item = items.Single(i => i.Title == title);
          Assert.Equal(expected, item.MimeType);
          using (var response = DlnaClient.Get(item.Url, userAgent, true)) {
            Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
          }
        }
        var source = DlnaClient.GetProtocolInfo(server.Port, prefix, userAgent);
        Assert.Contains(source, e => e.Contains($":{mkv}:"));
        Assert.Contains(source, e => e.Contains($":{webm}:"));
      }

      Expect(DlnaClient.GENERIC, "video/x-matroska", "video/webm");
      Expect(DlnaClient.SAMSUNG, "video/x-mkv", "video/x-mkv");
      Expect(DlnaClient.GENERIC, "video/x-matroska", "video/webm");
    }

    /// <summary>
    ///   Regression: browse results embed links built from the address the
    ///   client connected to, and the cache ignored that address, so a local
    ///   browse sent network clients to 127.0.0.1.
    /// </summary>
    [Fact]
    public void LinksUseTheAddressEachClientConnectedTo()
    {
      var other = DlnaTestServer.NonLoopbackAddress();
      if (other == null && OperatingSystem.IsLinux()) {
        // All of 127.0.0.0/8 is local on Linux, and a different local address
        // is all this needs.
        other = "127.0.0.2";
      }
      Assert.SkipWhen(other == null, "no second local address to connect through");
      media.WriteFile("film.mkv");
      var prefix = Mount();

      foreach (var host in new[] {"127.0.0.1", other, "127.0.0.1", other}) {
        var (_, _, didl) = DlnaClient.Browse(host, server.Port, prefix);
        Assert.Equal(new[] {$"{host}:{server.Port}"}, DlnaClient.LinkHosts(didl));
      }
    }

    /// <summary>
    ///   Regression: every response said "Connection: keep-alive", yet the
    ///   server closes the connection after answering unless the request asked
    ///   to keep it open. HTTP/1.1 clients believe the header and reuse the
    ///   connection, so a request sent before the close landed was lost: "The
    ///   response ended prematurely". It took a busy machine to hit, but CI
    ///   did.
    /// </summary>
    [Fact]
    public void ConnectionHeaderSaysWhetherTheConnectionStaysOpen()
    {
      media.WriteFile("film.mkv");
      var prefix = Mount();

      using (var client = new TcpClient("127.0.0.1", server.Port)) {
        var stream = client.GetStream();
        stream.ReadTimeout = 10000;

        var kept = DlnaClient.RawGet(stream, prefix + "description.xml", "Connection: keep-alive\r\n");
        Assert.Equal("keep-alive", kept["CONNECTION"]);

        // Still open, as announced; without asking again, it is closed.
        var closed = DlnaClient.RawGet(stream, prefix + "description.xml");
        Assert.Equal("close", closed["CONNECTION"]);
        Assert.Equal(-1, stream.ReadByte());
      }
    }

    [Fact]
    public void RestrictionsRefuseOtherMachinesButAlwaysAdmitThisOne()
    {
      var address = RequireNonLoopbackAddress();
      media.WriteFile("film.mkv");
      var fileServer = TestFileServer.Create(media.Info);
      var authorizer = new HttpAuthorizer();
      authorizer.AddMethod(new IPAddressAuthorizer(new[] {"10.255.255.1"}));
      fileServer.Authorizer = authorizer;
      var prefix = server.Mount(fileServer);

      Assert.Equal(HttpStatusCode.Forbidden, DlnaClient.Browse(address, server.Port, prefix).Status);
      Assert.Equal(HttpStatusCode.OK, DlnaClient.Browse("127.0.0.1", server.Port, prefix).Status);
    }

    [Fact]
    public void ConfiguredServerAppliesItsOwnSettings()
    {
      media.WriteFile("film.mkv");
      media.WriteFile("song.mp3");
      var config = new ServerConfiguration
      {
        Name = "Configured",
        SortOrder = "title",
        SortDirection = "asc"
      };
      config.Folders.Add(media.Path);
      config.Folders.Add(media.Combine("unmounted-drive"));
      config.MediaTypes.Add("video");
      config.Restrictions.Ips.Add("10.255.255.1");

      var fileServer = Program.SetupConfiguredServer(config, null, false, server.Http);
      var prefix = server.Mount(fileServer, true);

      // The missing folder was skipped rather than failing the server.
      var (status, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);
      Assert.Equal(HttpStatusCode.OK, status);
      Assert.Equal("film", Assert.Single(items).Title);

      var address = DlnaTestServer.NonLoopbackAddress();
      if (address != null) {
        Assert.Equal(HttpStatusCode.Forbidden, DlnaClient.Browse(address, server.Port, prefix).Status);
      }
    }

    /// <summary>
    ///   Works without ffmpeg since image thumbnails no longer depend on the
    ///   video loader constructing successfully.
    /// </summary>
    [Fact]
    public void ImageThumbnailIsServed()
    {
      TestMedia.WriteJpeg(media.Combine("photo.jpg"), 800, 600);
      var prefix = Mount();

      var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      Assert.True(TestMedia.IsJpeg(DlnaClient.GetBytes(Assert.Single(items).CoverUrl)));
    }

    private HttpStatusCode CoverStatus(string prefix, BrowsedItem item)
    {
      using (var response = DlnaClient.Get($"http://127.0.0.1:{server.Port}{prefix}cover/{item.Id}/i.jpg")) {
        return response.StatusCode;
      }
    }

    /// <summary>
    ///   Regression: an audio file without embedded art was listed with an
    ///   albumArtURI and an icon all the same, and fetching them failed with
    ///   a 500. The item has no cover, so there is nothing to link to, and a
    ///   link a client kept from elsewhere is not found.
    /// </summary>
    [Fact]
    public void AudioWithoutArtHasNoCover()
    {
      media.WriteFile("song.mp3");
      var prefix = Mount();

      var (_, items, didl) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      var item = Assert.Single(items);
      Assert.Null(item.CoverUrl);
      Assert.DoesNotContain("/cover/", didl);
      Assert.Equal(HttpStatusCode.NotFound, CoverStatus(prefix, item));
    }

    [Fact]
    public void AudioArtIsServed()
    {
      var art = media.Combine("art.jpg");
      TestMedia.WriteJpeg(art, 500, 500);
      TestMedia.WriteMp3WithArt(media.Combine("song.mp3"), File.ReadAllBytes(art));
      File.Delete(art);
      var prefix = Mount();

      var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      var item = Assert.Single(items);
      Assert.Equal("object.item.audioItem.musicTrack", item.Class);
      Assert.True(TestMedia.IsJpeg(DlnaClient.GetBytes(item.CoverUrl)));
    }

    /// <summary>
    ///   Regression: a thumbnail is only made when its link is fetched, and
    ///   when that failed (here, a file that isn't an image) the response was
    ///   a 500.
    /// </summary>
    [Fact]
    public void CoverThatCannotBeMadeIsNotFound()
    {
      media.WriteFile("photo.jpg");
      var prefix = Mount();

      var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      var item = Assert.Single(items);
      Assert.Equal(HttpStatusCode.NotFound, CoverStatus(prefix, item));
      // Failures aren't remembered, so asking again tries again.
      Assert.Equal(HttpStatusCode.NotFound, CoverStatus(prefix, item));
    }

    [Fact]
    public void VideoDurationAndThumbnailComeFromFFmpeg()
    {
      Assert.SkipUnless(TestMedia.HasFFmpeg, "ffmpeg is not installed");
      TestMedia.WriteVideo(media.Combine("clip.mp4"), 3);
      var prefix = Mount();

      var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);

      var item = Assert.Single(items);
      Assert.Matches(@"^\d+:\d\d:\d\d\.\d{3}$", item.Duration);
      var duration = TimeSpan.Parse(item.Duration, CultureInfo.InvariantCulture);
      Assert.InRange(duration.TotalSeconds, 2.5, 3.5);
      Assert.True(TestMedia.IsJpeg(DlnaClient.GetBytes(item.CoverUrl)));
    }

    /// <summary>
    ///   Regression: res@duration was formatted with the machine's culture, so
    ///   on an es-CO machine a 2.366 s song was announced as
    ///   duration="0:00:02,366". The HTML index shows the same text.
    /// </summary>
    [Fact]
    public void DurationIsSentWithADecimalPointInAnyCulture()
    {
      media.WriteFile("song.mp3");
      var fileServer = TestFileServer.Create(media.Info);
      var prefix = server.Mount(fileServer);
      var root = Assert.IsAssignableFrom<IMediaFolder>(fileServer.GetItem(Identifiers.GENERAL_ROOT));
      Assert.IsType<AudioFile>(Assert.Single(root.ChildItems))
        .Set("duration", (TimeSpan?)TimeSpan.FromMilliseconds(2366))
        .Set("initialized", true);

      // Responses are built on the server's threads, not this one.
      using (CultureScope.CommaDecimals(processWide: true)) {
        var (_, items, _) = DlnaClient.Browse("127.0.0.1", server.Port, prefix);
        var html = Encoding.UTF8.GetString(
          DlnaClient.GetBytes($"http://127.0.0.1:{server.Port}{prefix}index/{Identifiers.GENERAL_ROOT}"));

        Assert.Equal("0:00:02.366", Assert.Single(items).Duration);
        Assert.Contains("<td>0:00:02.366</td>", html);
      }
    }
  }
}
