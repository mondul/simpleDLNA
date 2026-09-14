using System;
using System.IO;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   The SQLite-backed metadata cache: Microsoft.Data.Sqlite with named
  ///   parameters, DBNull for a missing cover, and the schema check.
  /// </summary>
  public sealed class FileStoreTests : IDisposable
  {
    private readonly TempDirectory temp = new TempDirectory();

    private readonly FileServer server;

    public FileStoreTests()
    {
      server = TestFileServer.Create(temp.Info);
    }

    public void Dispose()
    {
      server.Dispose();
      temp.Dispose();
    }

    [Fact]
    public void StoredFileIsReadBackAfterReopeningTheDatabase()
    {
      var database = new FileInfo(temp.Combine("cache.db"));
      var media = temp.WriteFile("movie.mkv", 1024);
      var video = new VideoFile(server, media, DlnaMime.VideoMKV)
        .Set("title", "Cached")
        .Set("width", (int?)1280)
        .Set("height", (int?)720)
        .Set("subTitle", new Subtitle())
        .Set("initialized", true);

      using (var store = new FileStore(database)) {
        // No cover has been generated, so this also exercises binding a
        // missing cover as DBNull, which Microsoft.Data.Sqlite requires.
        store.MaybeStoreFile(video);
        Assert.False(store.HasCover(video));
      }

      using (var store = new FileStore(database)) {
        var cached = Assert.IsType<VideoFile>(
          store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
        Assert.Equal(1280, cached.MetaWidth);
        Assert.Equal(720, cached.MetaHeight);
      }
    }

    [Fact]
    public void ChangedFileIsNotServedFromTheCache()
    {
      var database = new FileInfo(temp.Combine("cache.db"));
      var media = temp.WriteFile("clip.mkv", 1024);
      var video = new VideoFile(server, media, DlnaMime.VideoMKV)
        .Set("subTitle", new Subtitle())
        .Set("initialized", true);

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(video);
      }

      // The cache is keyed by path, size and modification time.
      File.WriteAllBytes(media.FullName, new byte[2048]);
      media.Refresh();

      using (var store = new FileStore(database)) {
        Assert.Null(store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
      }
    }

    [Fact]
    public void DatabaseFromAnOlderSchemaIsReplaced()
    {
      var database = new FileInfo(temp.Combine("old.db"));
      File.WriteAllText(database.FullName, "not a SQLite database from this version");

      using (var store = new FileStore(database)) {
        var media = temp.WriteFile("a.mkv");
        store.MaybeStoreFile(
          new VideoFile(server, media, DlnaMime.VideoMKV)
            .Set("subTitle", new Subtitle())
            .Set("initialized", true));
        Assert.NotNull(store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
      }
    }
  }
}
