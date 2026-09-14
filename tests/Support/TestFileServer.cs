using System.IO;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Comparers;

namespace NMaier.SimpleDlna.Tests.Support
{
  internal static class TestFileServer
  {
    public static FileServer Create(DirectoryInfo directory,
      DlnaMediaTypes types = DlnaMediaTypes.All)
    {
      var ids = new Identifiers(ComparerRepository.Lookup("title"), false);
      return new FileServer(types, ids, directory);
    }
  }
}
