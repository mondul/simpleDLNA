using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LiteDB;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer
{
  /// <summary>
  ///   The metadata cache: each file's serialized metadata and cover, kept in
  ///   a LiteDB database and valid while the file's size and modification
  ///   time are unchanged.
  /// </summary>
  /// <remarks>
  ///   <para>
  ///     LiteDB is written in C#, so the cache needs no native library beside
  ///     the single-file executable. Each document is
  ///     <c>{_id, path, size, time, data, cover}</c>. The id is a SHA-256 hash
  ///     of the path rather than the path itself: LiteDB index keys are
  ///     limited to 1023 bytes, which a deep path can exceed.
  ///   </para>
  ///   <para>
  ///     LiteDB's direct mode must not have two engines on one file. Several
  ///     servers in this process share one database per file, and a lock file
  ///     beside it keeps other processes out; a second sdlna using the same
  ///     cache runs without one.
  ///   </para>
  /// </remarks>
  internal sealed class FileStore : Logging, IDisposable
  {
    private const string COLLECTION = "store";

    /// <summary>
    ///   Stored as the database's UserVersion. Bump it when a cached payload
    ///   changes (see <see cref="MediaSerializer" />): a database from another
    ///   version is discarded and rebuilt.
    /// </summary>
    internal const int SCHEMA = 0x20260916;

    private static readonly Dictionary<string, SharedDatabase> databases =
      new Dictionary<string, SharedDatabase>(StringComparer.Ordinal);

    private static readonly FileStoreVacuumer vacuumer =
      new FileStoreVacuumer();

    private readonly SharedDatabase shared;

    private bool disposed;

    public readonly FileInfo StoreFile;

    internal FileStore(FileInfo storeFile)
    {
      StoreFile = storeFile;
      shared = SharedDatabase.Acquire(storeFile, this);
      InfoFormat("FileStore at {0} is ready", storeFile.FullName);
      vacuumer.Add(this);
    }

    public void Dispose()
    {
      if (disposed) {
        return;
      }
      disposed = true;
      vacuumer.Remove(this);
      shared.Release();
    }

    /// <summary>
    ///   LiteDB's write-ahead log, kept beside the database while it is open.
    /// </summary>
    internal static string LogFileOf(FileInfo storeFile)
    {
      return Path.Combine(
        storeFile.DirectoryName ?? string.Empty,
        Path.GetFileNameWithoutExtension(storeFile.Name) + "-log" + storeFile.Extension);
    }

    internal static string LockFileOf(FileInfo storeFile)
    {
      return storeFile.FullName + ".lock";
    }

    /// <summary>
    ///   Takes the lock that keeps other processes out of the database, held
    ///   for as long as it is open.
    /// </summary>
    /// <exception cref="IOException">Another process holds it.</exception>
    internal static FileStream LockCache(FileInfo storeFile)
    {
      return new FileStream(
        LockFileOf(storeFile), FileMode.OpenOrCreate, FileAccess.ReadWrite,
        FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    /// <summary>
    ///   Whether <paramref name="path" /> is one of the files the cache keeps,
    ///   so that a cache inside a served folder does not trigger rescans.
    /// </summary>
    internal bool IsStoreFile(string path, StringComparer comparer)
    {
      return comparer.Equals(path, StoreFile.FullName) ||
             comparer.Equals(path, LogFileOf(StoreFile)) ||
             comparer.Equals(path, LockFileOf(StoreFile));
    }

    private static string KeyOf(string path)
    {
      return Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }

    /// <summary>
    ///   The entry for <paramref name="info" />, if it was stored for this
    ///   version of the file.
    /// </summary>
    private BsonDocument Find(FileInfo info, string operation)
    {
      if (disposed) {
        return null;
      }
      BsonDocument doc;
      try {
        doc = shared.Collection.FindById(KeyOf(info.FullName));
      }
      catch (Exception ex) when (ex is LiteException || ex is IOException) {
        Error($"Failed to look up {operation} in the store", ex);
        return null;
      }
      return IsCurrent(doc, info) ? doc : null;
    }

    private static bool IsCurrent(BsonDocument doc, FileInfo info)
    {
      return doc != null &&
             doc["path"].IsString && doc["path"].AsString == info.FullName &&
             doc["size"].IsInt64 && doc["size"].AsInt64 == info.Length &&
             doc["time"].IsInt64 &&
             doc["time"].AsInt64 == info.LastWriteTimeUtc.Ticks;
    }

    internal bool HasCover(BaseFile file)
    {
      var doc = Find(file.Item, "a file cover's existence");
      return doc != null && doc["cover"].IsBinary;
    }

    internal Cover MaybeGetCover(BaseFile file)
    {
      var info = file.Item;
      var doc = Find(info, "a file cover");
      if (doc == null || !doc["cover"].IsBinary) {
        return null;
      }
      try {
        using (var s = new MemoryStream(doc["cover"].AsBinary)) {
          return MediaSerializer.DeserializeCover(
            s, new DeserializeInfo(null, info, DlnaMime.ImageJPEG));
        }
      }
      catch (Exception ex) when (
        ex is InvalidDataException || ex is EndOfStreamException) {
        Debug("Failed to deserialize a cover", ex);
        return null;
      }
      catch (Exception ex) {
        Fatal("Failed to deserialize a cover", ex);
        throw;
      }
    }

    internal BaseFile MaybeGetFile(FileServer server, FileInfo info,
      DlnaMime type)
    {
      var doc = Find(info, "a file");
      if (doc == null || !doc["data"].IsBinary) {
        return null;
      }
      try {
        using (var s = new MemoryStream(doc["data"].AsBinary)) {
          var rv = MediaSerializer.DeserializeFile(
            s, new DeserializeInfo(server, info, type));
          rv.Item = info;
          return rv;
        }
      }
      catch (Exception ex) when (
        ex is InvalidDataException || ex is EndOfStreamException) {
        Debug("Failed to deserialize an item", ex);
        return null;
      }
    }

    internal void MaybeStoreFile(BaseFile file)
    {
      if (disposed) {
        return;
      }
      if (!MediaSerializer.CanSerialize(file)) {
        return;
      }
      try {
        using (var s = StreamManager.GetStream()) {
          using (var c = StreamManager.GetStream()) {
            MediaSerializer.Serialize(s, file);
            Cover cover = null;
            try {
              cover = file.MaybeGetCover();
              if (cover != null) {
                MediaSerializer.SerializeCover(c, cover);
              }
            }
            catch (NotSupportedException) {
              // Ignore and store no cover. Clearing the local is what makes
              // that happen: a throw part way through SerializeCover would
              // otherwise persist a truncated cover blob.
              cover = null;
            }

            var info = file.Item;
            var key = KeyOf(info.FullName);
            // Read, merge and write as one step, for servers sharing this
            // database.
            lock (shared) {
              try {
                var coverValue = cover != null
                  ? new BsonValue(c.ToArray())
                  : BsonValue.Null;
                if (cover == null) {
                  // Metadata is often stored again without the cover, which
                  // loads lazily and may since have been collected. Keep the
                  // stored one, but only for this version of the file: a
                  // changed file needs a new cover.
                  var existing = shared.Collection.FindById(key);
                  if (IsCurrent(existing, info) && existing["cover"].IsBinary) {
                    coverValue = existing["cover"];
                  }
                }
                shared.Collection.Upsert(new BsonDocument
                {
                  ["_id"] = key,
                  ["path"] = info.FullName,
                  ["size"] = info.Length,
                  ["time"] = info.LastWriteTimeUtc.Ticks,
                  ["data"] = s.ToArray(),
                  ["cover"] = coverValue
                });
              }
              catch (Exception ex) when (ex is LiteException || ex is IOException) {
                Error("Failed to put a file into the store", ex);
              }
            }
          }
        }
      }
      catch (Exception ex) {
        Error("Failed to serialize an object of type " + file.GetType(), ex);
        throw;
      }
    }

    /// <summary>
    ///   Removes the entries of files that no longer exist.
    /// </summary>
    internal void PurgeMissingFiles()
    {
      if (disposed) {
        return;
      }
      List<BsonDocument> entries;
      lock (shared) {
        entries = shared.Collection.Query()
          .Select("{_id, path}")
          .ToList();
      }
      var gone = entries
        .Where(e => !e["path"].IsString || !File.Exists(e["path"].AsString))
        .ToList();
      foreach (var entry in gone) {
        lock (shared) {
          shared.Collection.Delete(entry["_id"]);
        }
        DebugFormat("Purged {0}", entry["path"]);
      }
      DebugFormat("Purged {0} of {1} entries", gone.Count, entries.Count);
    }

    /// <summary>
    ///   One open database, shared by every FileStore on the same file.
    /// </summary>
    private sealed class SharedDatabase
    {
      private LiteDatabase database;

      private FileStream lockFile;

      private string path;

      private int references;

      public ILiteCollection<BsonDocument> Collection { get; private set; }

      public static SharedDatabase Acquire(FileInfo storeFile, Logging log)
      {
        var path = storeFile.FullName;
        lock (databases) {
          SharedDatabase rv;
          if (databases.TryGetValue(path, out rv)) {
            rv.references++;
            return rv;
          }
          // A second process gets an IOException here and runs without the
          // cache.
          var lockFile = LockCache(storeFile);
          try {
            var database = Open(storeFile, log);
            rv = new SharedDatabase
            {
              database = database,
              lockFile = lockFile,
              path = path,
              references = 1,
              Collection = database.GetCollection(COLLECTION)
            };
            databases.Add(path, rv);
            return rv;
          }
          catch (Exception) {
            lockFile.Dispose();
            throw;
          }
        }
      }

      public void Release()
      {
        lock (databases) {
          if (--references != 0) {
            return;
          }
          databases.Remove(path);
          database.Dispose();
          lockFile.Dispose();
        }
      }

      private static LiteDatabase Connect(FileInfo storeFile)
      {
        return new LiteDatabase(new ConnectionString
        {
          Filename = storeFile.FullName,
          Connection = ConnectionType.Direct,
          // Stored in the file when it is created. The default, the current
          // culture, cannot be loaded under invariant globalization (common in
          // containers), which would make the file unreadable there.
          Collation = new Collation("/Ordinal"),
          Upgrade = false
        });
      }

      private static LiteDatabase Open(FileInfo storeFile, Logging log)
      {
        try {
          var database = Connect(storeFile);
          try {
            if (database.UserVersion == SCHEMA) {
              return database;
            }
            if (database.UserVersion == 0 &&
                !database.GetCollectionNames().Any()) {
              database.UserVersion = SCHEMA;
              return database;
            }
            throw new InvalidDataException(string.Format(
              CultureInfo.InvariantCulture,
              "schema {0:X} instead of {1:X}", database.UserVersion, SCHEMA));
          }
          catch (Exception) {
            database.Dispose();
            throw;
          }
        }
        catch (Exception ex) when (
          ex is LiteException || ex is InvalidDataException ||
          ex is CultureNotFoundException) {
          // Another version's cache (including the SQLite caches of earlier
          // releases) or a damaged one: it is only a cache, so start over.
          log.NoticeFormat("Recreating the cache database. ({0})", ex.Message);
          Delete(storeFile);
          var database = Connect(storeFile);
          database.UserVersion = SCHEMA;
          return database;
        }
      }

      /// <summary>
      ///   Deletes the database with its LiteDB log, and the journals SQLite
      ///   kept beside the caches of earlier releases.
      /// </summary>
      private static void Delete(FileInfo storeFile)
      {
        var path = storeFile.FullName;
        foreach (var file in new[] {
          path, LogFileOf(storeFile), path + "-journal", path + "-wal", path + "-shm"
        }) {
          for (var i = 0; i < 10; ++i) {
            try {
              File.Delete(file);
              break;
            }
            catch (IOException) {
              Thread.Sleep(100);
            }
          }
        }
      }
    }
  }
}
