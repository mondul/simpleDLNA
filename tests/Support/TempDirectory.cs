using System;
using System.IO;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   A uniquely named scratch directory, deleted on dispose.
  /// </summary>
  internal sealed class TempDirectory : IDisposable
  {
    public TempDirectory()
    {
      Info = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sdlna-tests", Guid.NewGuid().ToString("N")));
    }

    public DirectoryInfo Info { get; }

    public string Path => Info.FullName;

    public void Dispose()
    {
      try {
        Info.Refresh();
        if (Info.Exists) {
          Info.Delete(true);
        }
      }
      catch (IOException) {
        // A server under test may still hold a file open briefly; a stray
        // temp directory is harmless.
      }
      catch (UnauthorizedAccessException) {
      }
    }

    public string Combine(params string[] parts)
    {
      var all = new string[parts.Length + 1];
      all[0] = Path;
      parts.CopyTo(all, 1);
      return System.IO.Path.Combine(all);
    }

    /// <summary>
    ///   Writes a file with arbitrary content. The server classifies files by
    ///   extension, so this is enough to list and announce them.
    /// </summary>
    public FileInfo WriteFile(string name, int size = 64)
    {
      var path = Combine(name);
      File.WriteAllBytes(path, new byte[size]);
      return new FileInfo(path);
    }
  }
}
