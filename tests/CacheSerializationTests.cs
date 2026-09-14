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
  ///   The binary format that replaced BinaryFormatter for the file store.
  /// </summary>
  public sealed class CacheSerializationTests : IDisposable
  {
    private readonly TempDirectory temp = new TempDirectory();

    private readonly FileServer server;

    public CacheSerializationTests()
    {
      server = TestFileServer.Create(temp.Info);
    }

    public void Dispose()
    {
      server.Dispose();
      temp.Dispose();
    }

    private BaseFile RoundTrip(BaseFile file, DlnaMime type)
    {
      using (var stream = new MemoryStream()) {
        MediaSerializer.Serialize(stream, file);
        stream.Position = 0;
        return MediaSerializer.DeserializeFile(
          stream, new DeserializeInfo(server, file.Item, type));
      }
    }

    [Fact]
    public void AudioFileRoundTripsIncludingNullsAndUnicode()
    {
      var fi = temp.WriteFile("song.mp3");
      var audio = new AudioFile(server, fi, DlnaMime.AudioMP3)
        .Set("album", "Album Ω")
        .Set("artist", "Artist")
        .Set("genre", null)
        .Set("performer", "Performer")
        .Set("title", "Title")
        .Set("track", (int?)7)
        .Set("duration", (TimeSpan?)TimeSpan.FromSeconds(212.5))
        .Set("initialized", true);

      var copy = Assert.IsType<AudioFile>(RoundTrip(audio, DlnaMime.AudioMP3));

      Assert.Equal("Album Ω", copy.MetaAlbum);
      Assert.Equal("Artist", copy.MetaArtist);
      Assert.Null(copy.MetaGenre);
      Assert.Equal("Performer", copy.MetaPerformer);
      Assert.Equal(7, copy.MetaTrack);
      Assert.Equal(TimeSpan.FromSeconds(212.5), copy.MetaDuration);
    }

    /// <summary>
    ///   Regression: the (SerializationInfo, StreamingContext) constructor that
    ///   BinaryFormatter called read no fields, so cached image metadata was
    ///   always discarded.
    /// </summary>
    [Fact]
    public void ImageFileRoundTripsItsMetadata()
    {
      var fi = temp.WriteFile("photo.jpg");
      var image = new ImageFile(server, fi, DlnaMime.ImageJPEG)
        .Set("creator", "Creator")
        .Set("description", null)
        .Set("title", "Photo")
        .Set("width", (int?)1920)
        .Set("height", (int?)1080)
        .Set("initialized", true);

      var copy = Assert.IsType<ImageFile>(RoundTrip(image, DlnaMime.ImageJPEG));

      Assert.Equal("Creator", copy.MetaCreator);
      Assert.Null(copy.MetaDescription);
      Assert.Equal(1920, copy.MetaWidth);
      Assert.Equal(1080, copy.MetaHeight);
    }

    [Fact]
    public void VideoFileRoundTripsIncludingSubtitleAndBookmark()
    {
      const string srt = "1\n00:00:01,000 --> 00:00:02,000\nhi\n";
      var fi = temp.WriteFile("movie.mkv");
      var video = new VideoFile(server, fi, DlnaMime.VideoMKV)
        .Set("actors", new[] {"Ann", "Bob", null})
        .Set("description", "Desc")
        .Set("director", "Dir")
        .Set("genre", "Genre")
        .Set("title", "Movie")
        .Set("width", (int?)3840)
        .Set("height", (int?)2160)
        .Set("bookmark", (long?)1234)
        .Set("duration", (TimeSpan?)TimeSpan.FromMinutes(97))
        .Set("subTitle", new Subtitle(srt))
        .Set("initialized", true);

      var copy = Assert.IsType<VideoFile>(RoundTrip(video, DlnaMime.VideoMKV));

      Assert.Equal(new[] {"Ann", "Bob", null}, copy.MetaActors.ToArray());
      Assert.Equal("Desc", copy.MetaDescription);
      Assert.Equal("Dir", copy.MetaDirector);
      Assert.Equal("Genre", copy.MetaGenre);
      Assert.Equal(3840, copy.MetaWidth);
      Assert.Equal(2160, copy.MetaHeight);
      Assert.Equal(1234, copy.Bookmark);
      Assert.Equal(TimeSpan.FromMinutes(97), copy.MetaDuration);
      Assert.Equal(srt, copy.Subtitle.Text);
    }

    /// <summary>
    ///   Regression: a probed-but-empty subtitle used to come back as null,
    ///   which VideoFile treats as "not looked yet", re-running ffmpeg on every
    ///   listing.
    /// </summary>
    [Fact]
    public void ProbedEmptySubtitleSurvivesSoItIsNotProbedAgain()
    {
      var fi = temp.WriteFile("nosubs.mkv");
      var video = new VideoFile(server, fi, DlnaMime.VideoMKV)
        .Set("subTitle", new Subtitle())
        .Set("initialized", true);

      var copy = RoundTrip(video, DlnaMime.VideoMKV);

      var subtitle = typeof (VideoFile)
        .GetField("subTitle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .GetValue(copy) as Subtitle;
      Assert.NotNull(subtitle);
      Assert.False(subtitle.HasSubtitle);
    }

    [Fact]
    public void CoverRoundTrips()
    {
      var fi = temp.WriteFile("cover.jpg");
      var payload = new byte[] {1, 2, 3, 250, 251, 252};
      var cover = new Cover(fi)
        .Set("bytes", payload)
        .Set("width", 384)
        .Set("height", 216);

      Cover copy;
      using (var stream = new MemoryStream()) {
        MediaSerializer.SerializeCover(stream, cover);
        stream.Position = 0;
        copy = MediaSerializer.DeserializeCover(
          stream, new DeserializeInfo(server, fi, DlnaMime.ImageJPEG));
      }

      Assert.Equal(384, copy.MetaWidth);
      Assert.Equal(216, copy.MetaHeight);
      using (var content = copy.CreateContentStream()) {
        using (var buffer = new MemoryStream()) {
          content.CopyTo(buffer);
          Assert.Equal(payload, buffer.ToArray());
        }
      }
    }

    /// <summary>
    ///   FileStore treats these two exception types as "not cached" rather
    ///   than a crash, so corrupt input must produce exactly them.
    /// </summary>
    [Theory]
    [InlineData("bad version", new byte[] {99, 1, 0}, typeof (InvalidDataException))]
    [InlineData("unknown kind", new byte[] {1, 77}, typeof (InvalidDataException))]
    [InlineData("truncated", new byte[] {1, 1, 1}, typeof (EndOfStreamException))]
    public void CorruptInputRaisesTheExceptionsFileStoreCatches(string label,
      byte[] blob, Type expected)
    {
      var fi = temp.WriteFile("corrupt.mp3");
      using (var stream = new MemoryStream(blob)) {
        var ex = Record.Exception(
          () => MediaSerializer.DeserializeFile(
            stream, new DeserializeInfo(server, fi, DlnaMime.AudioMP3)));
        Assert.True(ex != null && ex.GetType() == expected,
                    $"{label}: expected {expected.Name}, got {ex?.GetType().Name ?? "no exception"}");
      }
    }

    [Fact]
    public void AudioImageAndVideoFilesAreCacheable()
    {
      Assert.True(MediaSerializer.CanSerialize(
        new AudioFile(server, temp.WriteFile("a.mp3"), DlnaMime.AudioMP3)));
      Assert.True(MediaSerializer.CanSerialize(
        new ImageFile(server, temp.WriteFile("a.jpg"), DlnaMime.ImageJPEG)));
      Assert.True(MediaSerializer.CanSerialize(
        new VideoFile(server, temp.WriteFile("a.mkv"), DlnaMime.VideoMKV)));
    }
  }
}
