using System;
using System.IO;
using System.Linq;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   How a file server's listing follows the file system watcher.
  /// </summary>
  public sealed class FileServerTests : IDisposable
  {
    private readonly TempDirectory media = new TempDirectory();

    public void Dispose()
    {
      media.Dispose();
    }

    private FileServer Load()
    {
      var server = TestFileServer.Create(media.Info);
      server.Rescanning = false;
      server.Load();
      return server;
    }

    private static string[] Listed(FileServer server)
    {
      var root = (IMediaFolder)server.GetItem(Identifiers.GENERAL_ROOT);
      return root.ChildItems.Select(i => i.Title).OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }

    private void Created(FileServer server, string name)
    {
      server.OnChanged(null, new FileSystemEventArgs(WatcherChangeTypes.Created, media.Path, name));
    }

    /// <summary>
    ///   Regression: a Created event for a file the server already listed
    ///   added it a second time. On macOS a watcher reports files created just
    ///   before it started, so files written right before a server loaded were
    ///   sometimes listed twice (seen in CI).
    /// </summary>
    [Fact]
    public void WatcherReportingAListedFileDoesNotListItTwice()
    {
      media.WriteFile("film.mkv");
      using (var server = Load()) {
        Created(server, "film.mkv");
        Created(server, "film.mkv");

        Assert.Equal(new[] {"film"}, Listed(server));
      }
    }

    [Fact]
    public void FileCreatedAfterLoadingIsListed()
    {
      media.WriteFile("film.mkv");
      using (var server = Load()) {
        media.WriteFile("clip.mkv");
        Created(server, "clip.mkv");

        Assert.Equal(new[] {"clip", "film"}, Listed(server));
      }
    }
  }
}
