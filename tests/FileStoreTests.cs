using System;
using System.IO;
using System.Linq;
using System.Text;
using LiteDB;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   The metadata cache, a LiteDB database: entries valid for one version of
  ///   a file, schema checks, and how the database file is shared.
  /// </summary>
  public sealed class FileStoreTests : IDisposable
  {
    private readonly FileInfo database;

    private readonly TempDirectory temp = new TempDirectory();

    private readonly FileServer server;

    public FileStoreTests()
    {
      server = TestFileServer.Create(temp.Info);
      database = new FileInfo(temp.Combine("cache.db"));
    }

    public void Dispose()
    {
      server.Dispose();
      temp.Dispose();
    }

    private VideoFile Video(FileInfo media)
    {
      return new VideoFile(server, media, DlnaMime.VideoMKV)
        .Set("subTitle", new Subtitle())
        .Set("initialized", true);
    }

    /// <summary>
    ///   A video whose cover has been generated. The caller keeps the cover
    ///   alive: files only hold it through a weak reference.
    /// </summary>
    private VideoFile VideoWithCover(FileInfo media, out object cover)
    {
      var image = TestMedia.WriteJpeg(temp.Combine(Guid.NewGuid() + ".jpg"), 64, 48);
      using (var stream = image.OpenRead()) {
        cover = Activator.CreateInstance(
          typeof (VideoFile).Assembly.GetType("NMaier.SimpleDlna.FileMediaServer.Cover"),
          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
          null, new object[] {media, stream}, null);
      }
      return Video(media).Set("weakCover", new WeakReference(cover));
    }

    [Fact]
    public void StoredFileIsReadBackAfterReopeningTheDatabase()
    {
      var media = temp.WriteFile("movie.mkv", 1024);
      var video = Video(media)
        .Set("title", "Cached")
        .Set("width", (int?)1280)
        .Set("height", (int?)720);

      using (var store = new FileStore(database)) {
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
      var media = temp.WriteFile("clip.mkv", 1024);

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(Video(media));
      }

      // The cache is keyed by path, size and modification time.
      File.WriteAllBytes(media.FullName, new byte[2048]);
      media.Refresh();

      using (var store = new FileStore(database)) {
        Assert.Null(store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
      }
    }

    [Fact]
    public void CoverIsStoredAndReadBack()
    {
      var media = temp.WriteFile("film.mkv", 1024);
      object cover;
      var video = VideoWithCover(media, out cover);

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(video);
      }

      using (var store = new FileStore(database)) {
        var file = Video(media);
        Assert.True(store.HasCover(file));
        var cached = store.MaybeGetCover(file);
        Assert.NotNull(cached);
        using (var data = cached.CreateContentStream()) {
          var bytes = new byte[3];
          data.ReadExactly(bytes);
          Assert.True(TestMedia.IsJpeg(bytes.Concat(new byte[1]).ToArray()));
        }
      }
      GC.KeepAlive(cover);
    }

    /// <summary>
    ///   Metadata is stored again later without the cover, which loads lazily;
    ///   the stored cover is kept for that.
    /// </summary>
    [Fact]
    public void StoringAgainWithoutTheCoverKeepsIt()
    {
      var media = temp.WriteFile("film.mkv", 1024);
      object cover;

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(VideoWithCover(media, out cover));
        store.MaybeStoreFile(Video(media));

        Assert.True(store.HasCover(Video(media)));
      }
      GC.KeepAlive(cover);
    }

    /// <summary>
    ///   Regression: the SQLite store kept the previous cover whenever a file
    ///   was stored without one, even after the file had changed. The stale
    ///   cover then matched the new size and time, so it was never replaced.
    /// </summary>
    [Fact]
    public void ChangedFileDoesNotKeepItsOldCover()
    {
      var media = temp.WriteFile("film.mkv", 1024);
      object cover;

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(VideoWithCover(media, out cover));

        File.WriteAllBytes(media.FullName, new byte[2048]);
        media.Refresh();
        store.MaybeStoreFile(Video(media));

        Assert.NotNull(store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
        Assert.False(store.HasCover(Video(media)));
      }
      GC.KeepAlive(cover);
    }

    [Fact]
    public void SqliteCacheFromEarlierReleasesIsReplaced()
    {
      var header = Encoding.ASCII.GetBytes("SQLite format 3\0");
      File.WriteAllBytes(database.FullName, header.Concat(new byte[8176]).ToArray());
      var media = temp.WriteFile("a.mkv");

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(Video(media));
        Assert.NotNull(store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
      }
      Assert.False(File.ReadAllBytes(database.FullName).AsSpan().StartsWith(header));
    }

    [Fact]
    public void DatabaseFromAnotherSchemaIsReplaced()
    {
      using (var other = new LiteDatabase($"Filename={database.FullName};Collation=/Ordinal")) {
        other.GetCollection("store").Insert(new BsonDocument {["_id"] = "stale"});
        other.UserVersion = FileStore.SCHEMA - 1;
      }

      using (var store = new FileStore(database)) {
      }

      using (var reopened = new LiteDatabase($"Filename={database.FullName}")) {
        Assert.Equal(FileStore.SCHEMA, reopened.UserVersion);
        Assert.Null(reopened.GetCollection("store").FindById("stale"));
      }
    }

    [Fact]
    public void StoresOnTheSameFileShareOneDatabase()
    {
      var media = temp.WriteFile("shared.mkv");
      var first = new FileStore(database);
      using (var second = new FileStore(database)) {
        first.MaybeStoreFile(Video(media));
        first.Dispose();

        // Still open for the second store, and still locked.
        Assert.NotNull(second.MaybeGetFile(server, media, DlnaMime.VideoMKV));
        Assert.True(File.Exists(FileStore.LockFileOf(database)));
      }
      Assert.False(File.Exists(FileStore.LockFileOf(database)));
    }

    /// <summary>
    ///   LiteDB lets a second process open the same file and write to it,
    ///   which corrupts it. The lock file prevents that; the second sdlna runs
    ///   without a cache (FileServer catches the exception) and must leave the
    ///   database alone.
    /// </summary>
    [Fact]
    public void CacheHeldByAnotherProcessIsLeftAlone()
    {
      var media = temp.WriteFile("held.mkv");
      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(Video(media));
      }
      var before = File.ReadAllBytes(database.FullName);

      // Taken the way another sdlna process would take it.
      using (FileStore.LockCache(database)) {
        Assert.Throws<IOException>(() => new FileStore(database));
      }

      Assert.Equal(before, File.ReadAllBytes(database.FullName));
    }

    /// <summary>
    ///   Entries are keyed by a hash of the path: LiteDB index keys are limited
    ///   to 1023 bytes, and its default collation ignores case.
    /// </summary>
    [Fact]
    public void LongPathsAreStored()
    {
      var directory = temp.Path;
      try {
        while (Encoding.UTF8.GetByteCount(directory) < 1100) {
          directory = Directory.CreateDirectory(
            Path.Combine(directory, new string('d', 100))).FullName;
        }
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
        Assert.Skip($"this file system cannot hold a path that long: {ex.Message}");
      }
      var media = new FileInfo(Path.Combine(directory, "deep.mkv"));
      File.WriteAllBytes(media.FullName, new byte[16]);

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(Video(media));
        Assert.NotNull(store.MaybeGetFile(server, media, DlnaMime.VideoMKV));
      }
    }

    [Fact]
    public void PathsDifferingOnlyInCaseAreSeparateEntries()
    {
      var lower = temp.WriteFile("case.mkv", 16);
      var upper = new FileInfo(temp.Combine("CASE.mkv"));
      Assert.SkipWhen(upper.Exists, "this file system ignores case");
      File.WriteAllBytes(upper.FullName, new byte[16]);
      upper.Refresh();
      File.SetLastWriteTimeUtc(upper.FullName, lower.LastWriteTimeUtc);
      upper.Refresh();

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(Video(lower).Set("title", "lower"));
        store.MaybeStoreFile(Video(upper).Set("title", "upper"));

        Assert.EndsWith("lower",
          store.MaybeGetFile(server, lower, DlnaMime.VideoMKV).Title);
        Assert.EndsWith("upper",
          store.MaybeGetFile(server, upper, DlnaMime.VideoMKV).Title);
      }
    }

    [Fact]
    public void PurgeRemovesEntriesOfDeletedFiles()
    {
      var kept = temp.WriteFile("kept.mkv");
      var deleted = temp.WriteFile("deleted.mkv");

      using (var store = new FileStore(database)) {
        store.MaybeStoreFile(Video(kept));
        store.MaybeStoreFile(Video(deleted));
        File.Delete(deleted.FullName);

        store.PurgeMissingFiles();
      }

      using (var db = new LiteDatabase($"Filename={database.FullName}")) {
        Assert.Equal(new[] {kept.FullName},
          db.GetCollection("store").FindAll().Select(d => d["path"].AsString).ToArray());
      }
    }

    [Fact]
    public void CacheFilesDoNotCountAsMediaChanges()
    {
      using (var store = new FileStore(database)) {
        var ignoreCase = StringComparer.OrdinalIgnoreCase;
        Assert.True(store.IsStoreFile(database.FullName, ignoreCase));
        Assert.True(store.IsStoreFile(temp.Combine("cache-log.db"), ignoreCase));
        Assert.True(store.IsStoreFile(temp.Combine("cache.db.lock"), ignoreCase));
        Assert.False(store.IsStoreFile(temp.Combine("cache.mkv"), ignoreCase));
      }
    }
  }
}
