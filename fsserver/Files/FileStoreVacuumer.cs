using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer
{
  /// <summary>
  ///   Every half hour to four hours, removes the cache entries of files that
  ///   no longer exist. (With SQLite it also ran VACUUM; LiteDB reuses freed
  ///   pages instead.)
  /// </summary>
  internal sealed class FileStoreVacuumer : Logging, IDisposable
  {
    private const int MAX_TIME = 240 * 60 * 1000;

    private const int MIN_TIME = 30 * 60 * 1000;

    private readonly List<WeakReference> stores = new List<WeakReference>();

    private readonly Random rnd = new Random();

    private readonly Timer timer = new Timer();

    public FileStoreVacuumer()
    {
      timer.Elapsed += Run;
      Schedule();
    }

    public void Dispose()
    {
      timer?.Dispose();
    }

    private void Run(object sender, ElapsedEventArgs e)
    {
      // Stores sharing a database file are purged once.
      FileStore[] current;
      lock (stores) {
        current = (from s in stores
                   let store = s.Target as FileStore
                   where store != null
                   group store by store.StoreFile.FullName
                   into sameFile
                   select sameFile.First()).ToArray();
      }
      if (current.Length != 0) {
        Task.Factory.StartNew(() =>
        {
          foreach (var store in current) {
            try {
              store.PurgeMissingFiles();
            }
            catch (Exception ex) {
              Error("Failed to purge a store", ex);
            }
          }
        }, TaskCreationOptions.LongRunning);
      }
      Schedule();
    }

    private void Schedule()
    {
      timer.Interval = rnd.Next(MIN_TIME, MAX_TIME);
      timer.Enabled = true;
      DebugFormat("Scheduling next purge in {0}", timer.Interval);
    }

    public void Add(FileStore store)
    {
      lock (stores) {
        stores.Add(new WeakReference(store));
      }
    }

    public void Remove(FileStore store)
    {
      lock (stores) {
        stores.RemoveAll(s => s.Target == null || s.Target == store);
      }
    }
  }
}
