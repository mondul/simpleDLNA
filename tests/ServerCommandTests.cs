using System;
using System.IO;
using System.Linq;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   "sdlna --server": exit codes are 0 on success, 1 for an invalid value
  ///   or conflict, and 2 for a malformed command line.
  /// </summary>
  [Collection(ProcessStateCollection.NAME)]
  public sealed class ServerCommandTests : IDisposable
  {
    private readonly ConfigurationHome home = new ConfigurationHome();

    private readonly string movies;

    public ServerCommandTests()
    {
      movies = home.Folder("movies");
    }

    public void Dispose()
    {
      home.Dispose();
    }

    private static CommandResult Run(params string[] args)
    {
      return ServerCommandRunner.Run(args);
    }

    private static CommandResult Succeeds(params string[] args)
    {
      var result = Run(args);
      Assert.True(result.ExitCode == 0, result.ToString());
      return result;
    }

    private static void Fails(int exitCode, params string[] args)
    {
      var result = Run(args);
      Assert.True(result.ExitCode == exitCode, $"expected exit {exitCode}; {result}");
    }

    private static ServerConfiguration Server(string name = "Movies")
    {
      return ConfigurationStore.Load().Servers.Single(s => s.Name == name);
    }

    [Fact]
    public void AddCreatesAServerWithTheDocumentedDefaults()
    {
      Succeeds("add", "Movies", movies);

      var server = Server();
      Assert.Equal(new[] {movies}, server.Folders);
      Assert.Equal(new[] {"video", "audio", "images"}, server.MediaTypes);
      Assert.Equal("title", server.SortOrder);
      Assert.Equal("asc", server.SortDirection);
      Assert.Empty(server.Views);
      Assert.True(server.Restrictions.IsEmpty);
      home.ReadConfig().Dispose();
    }

    [Fact]
    public void AddRejectsBadInput()
    {
      Succeeds("add", "Movies", movies);

      Fails(1, "add", "movies", movies);                          // duplicate, ignoring case
      Fails(1, "add", "Other", Path.Combine(home.Path, "nope"));  // missing folder
      Fails(2, "add", "Other");                                   // no folder
      Fails(2, "add", "Other", movies, "--views", "add", "music"); // options belong to config
    }

    [Fact]
    public void RemoveDeletesTheServer()
    {
      Succeeds("add", "Movies", movies);

      Succeeds("remove", "MOVIES");

      Assert.Empty(ConfigurationStore.Load().Servers);
      Fails(1, "remove", "Movies");
    }

    [Fact]
    public void MediaTypesAreSetAndUnsetButOneAlwaysRemains()
    {
      Succeeds("add", "Movies", movies);

      Succeeds("config", "Movies", "--media-types", "unset", "audio", "image");
      Assert.Equal(new[] {"video"}, Server().MediaTypes);

      Fails(1, "config", "Movies", "--media-types", "unset", "video");
      Fails(2, "config", "Movies", "--media-types", "set", "movies");

      Succeeds("config", "Movies", "--media-types", "set", "images");
      Assert.Equal(new[] {"video", "images"}, Server().MediaTypes);
    }

    [Fact]
    public void SortOrderTakesAFieldAndAnOptionalDirection()
    {
      Succeeds("add", "Movies", movies);

      Succeeds("config", "Movies", "--sort-order", "date", "desc");
      Assert.Equal(("date", "desc"), (Server().SortOrder, Server().SortDirection));

      Succeeds("config", "Movies", "--sort-order", "size");
      Assert.Equal(("size", "asc"), (Server().SortOrder, Server().SortDirection));

      Fails(2, "config", "Movies", "--sort-order", "name");
      Fails(2, "config", "Movies", "--sort-order", "date", "sideways");
    }

    [Fact]
    public void FoldersAreAddedAndRemovedButOneAlwaysRemains()
    {
      var music = home.Folder("music");
      Succeeds("add", "Movies", movies);

      Succeeds("config", "Movies", "--folders", "add", music);
      Assert.Equal(new[] {movies, music}, Server().Folders);

      Succeeds("config", "Movies", "--folders", "remove", movies);
      Assert.Equal(new[] {music}, Server().Folders);

      Fails(1, "config", "Movies", "--folders", "remove", music);
      Fails(1, "config", "Movies", "--folders", "remove", movies);
      Fails(1, "config", "Movies", "--folders", "add", Path.Combine(home.Path, "nope"));
    }

    [Fact]
    public void SameFolderThroughASymlinkIsNotAddedTwice()
    {
      var link = Path.Combine(home.Path, "movies-link");
      try {
        Directory.CreateSymbolicLink(link, movies);
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
        Assert.Skip($"cannot create symbolic links here: {ex.Message}");
      }
      Succeeds("add", "Movies", movies);

      var result = Succeeds("config", "Movies", "--folders", "add", link);

      Assert.Contains("is the same folder as", result.Output);
      Assert.Single(Server().Folders);
    }

    [Fact]
    public void ViewsAreAddedInOrderAndRemovedByValueOrName()
    {
      Succeeds("add", "Movies", movies);

      Succeeds("config", "Movies", "--views", "add", "new", "large:size=1000", "series:no-cascade");
      Assert.Equal(new[] {"new", "large:size=1000", "series:no-cascade"}, Server().Views);

      Succeeds("config", "Movies", "--views", "remove", "large");
      Assert.Equal(new[] {"new", "series:no-cascade"}, Server().Views);

      Fails(1, "config", "Movies", "--views", "add", "musik");
      Fails(1, "config", "Movies", "--views", "remove", "bytitle");
      Fails(2, "config", "Movies", "--views", "add");
    }

    /// <summary>
    ///   The grammar from the feature request: "--" ends a group, after which
    ///   another kind may follow, or the verb may change.
    /// </summary>
    [Fact]
    public void RestrictionGroupsAreSeparatedByDoubleDash()
    {
      Succeeds("add", "Movies", movies);

      Succeeds("config", "Movies", "--restrictions", "add",
               "--mac", "00:00:00:00:00:00", "11-11-11-11-11-11", "--",
               "--ip", "192.168.1.2", "192.168.1.3", "--",
               "--user-agent", "Mozilla/Whatever");
      var r = Server().Restrictions;
      Assert.Equal(new[] {"00:00:00:00:00:00", "11:11:11:11:11:11"}, r.Macs);
      Assert.Equal(new[] {"192.168.1.2", "192.168.1.3"}, r.Ips);
      Assert.Equal(new[] {"Mozilla/Whatever"}, r.UserAgents);

      Succeeds("config", "Movies", "--restrictions",
               "remove", "--ip", "192.168.1.3", "--",
               "add", "--ip", "10.0.0.1", "--",
               "remove", "--mac", "11:11:11:11:11:11");
      r = Server().Restrictions;
      Assert.Equal(new[] {"00:00:00:00:00:00"}, r.Macs);
      Assert.Equal(new[] {"192.168.1.2", "10.0.0.1"}, r.Ips);
    }

    [Fact]
    public void RestrictionsRejectMalformedEntries()
    {
      Succeeds("add", "Movies", movies);

      Fails(1, "config", "Movies", "--restrictions", "add", "--mac", "00:00:00:00");
      Fails(1, "config", "Movies", "--restrictions", "add", "--ip", "300.1.1.1");
      Fails(1, "config", "Movies", "--restrictions", "remove", "--ip", "8.8.8.8");
      Fails(2, "config", "Movies", "--restrictions", "add", "192.168.1.9");
      Fails(2, "config", "Movies", "--restrictions", "add", "--ip");
      Fails(2, "config", "Movies", "--restrictions", "--ip", "192.168.1.9");
    }

    [Fact]
    public void MacRestrictionWarnsWhereMacsCannotBeLookedUp()
    {
      Succeeds("add", "Movies", movies);

      var result = Succeeds("config", "Movies", "--restrictions", "add", "--mac", "01:AF:BC:00:0A:FF");

      Assert.Equal(!OperatingSystem.IsWindows(), result.Output.Contains("only be looked up when sdlna runs on Windows"));
    }

    [Fact]
    public void NothingIsSavedWhenAnyOptionFails()
    {
      Succeeds("add", "Movies", movies);
      var before = home.ConfigBytes();

      Fails(2, "config", "Movies", "--views", "add", "music", "--sort-order", "bogus");

      Assert.Equal(before, home.ConfigBytes());
    }

    [Fact]
    public void OptionWithoutValuesPrintsTheCurrentSetting()
    {
      Succeeds("add", "Movies", movies);
      Succeeds("config", "Movies", "--views", "add", "music");

      var result = Succeeds("config", "Movies", "--views");

      Assert.Contains("Views of \"Movies\"", result.Output);
      Assert.Contains("music", result.Output);
    }

    [Fact]
    public void OptionsThatChangeAServerNeedItsName()
    {
      Fails(2, "config", "--views", "add", "music");
      Fails(1, "config", "Nope", "--views");
      Fails(2, "bogus");
    }

    [Fact]
    public void EditorIsSetShownAndReset()
    {
      Succeeds("config", "--editor", "code", "--wait");
      Assert.Equal("code --wait", ConfigurationStore.Load().Editor);

      Assert.Contains("code --wait", Succeeds("config", "--editor").Output);

      Succeeds("config", "--editor", "default");
      Assert.Null(ConfigurationStore.Load().Editor);
    }

    private static void SkipUnlessPosixShell()
    {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "uses /bin/sh as a stand-in editor");
    }

    [Fact]
    public void EditOpensTheEditorAndValidatesTheResult()
    {
      SkipUnlessPosixShell();
      Succeeds("add", "Movies", movies);
      Succeeds("config", "--editor", "sh -c true");

      var result = Succeeds("config", "--edit");

      Assert.Contains("is valid (1 server)", result.Output);
    }

    [Fact]
    public void EditStillOpensABrokenFile()
    {
      SkipUnlessPosixShell();
      Succeeds("add", "Movies", movies);
      File.WriteAllText(home.ConfigPath, "{ broken");

      Fails(1, "config", "Movies", "--views");

      // The editor here changes nothing, so the file is still reported invalid
      // afterwards -- but it was opened, which is the point.
      var result = Run("config", "--edit", "--editor", "sh -c true");
      Assert.Contains("Warning:", result.Error);
      Assert.Contains("is not valid JSON", result.Error);
      Assert.Equal(1, result.ExitCode);
    }
  }
}
