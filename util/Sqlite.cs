using System;
using System.Data;
using System.IO;
using log4net;
using Microsoft.Data.Sqlite;

namespace NMaier.SimpleDlna.Utilities
{
  public static class Sqlite
  {
    public static void ClearPool(IDbConnection conn)
    {
      var sqlite = conn as SqliteConnection;
      if (sqlite != null) {
        SqliteConnection.ClearPool(sqlite);
      }
    }

    public static IDbConnection GetDatabaseConnection(FileInfo database)
    {
      if (database == null) {
        throw new ArgumentNullException(nameof(database));
      }
      if (database.Exists && database.IsReadOnly) {
        throw new ArgumentException(
          "Database file is read only",
          nameof(database)
          );
      }

      var cs = new SqliteConnectionStringBuilder
      {
        DataSource = database.FullName,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = 5
      }.ConnectionString;

      var rv = new SqliteConnection(cs);
      rv.Open();
      try {
        using (var pragma = rv.CreateCommand()) {
          // System.Data.SQLite accepted "Synchronous" and "journal mode" as
          // connection string keywords. Microsoft.Data.Sqlite does not, so
          // they are applied as pragmas once the connection is open.
          pragma.CommandText =
            "PRAGMA synchronous=OFF; PRAGMA journal_mode=TRUNCATE;";
          pragma.ExecuteNonQuery();
        }
      }
      catch (Exception ex) {
        LogManager.GetLogger(typeof (Sqlite)).Error(
          "Failed to configure sqlite connection", ex);
      }
      return rv;
    }
  }
}
