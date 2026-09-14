using System;
using System.IO;
using System.Linq;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   Reading, validating and writing ~/.sdlna/config.json.
  /// </summary>
  [Collection(ProcessStateCollection.NAME)]
  public sealed class ConfigurationTests : IDisposable
  {
    private readonly ConfigurationHome home = new ConfigurationHome();

    public void Dispose()
    {
      home.Dispose();
    }

    private ConfigurationException LoadFails(string json)
    {
      home.WriteConfig(json);
      return Assert.Throws<ConfigurationException>(() => ConfigurationStore.Load());
    }

    [Fact]
    public void NoFileMeansNoConfiguration()
    {
      Assert.Null(ConfigurationStore.Load());
    }

    [Fact]
    public void SavedFileIsStrictJsonAndLoadsBackWithTheDocumentedDefaults()
    {
      var config = ConfigurationStore.CreateDefault();
      var server = ConfigurationStore.CreateServer("Movies");
      server.Folders.Add(home.Folder("movies"));
      config.Servers.Add(server);

      ConfigurationStore.Save(config);

      using (var json = home.ReadConfig()) {
        Assert.Equal(0, json.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(ConfigurationStore.DefaultCachePath, json.RootElement.GetProperty("cache").GetString());
      }
      var loaded = ConfigurationStore.Load();
      var copy = Assert.Single(loaded.Servers);
      Assert.Equal("Movies", copy.Name);
      Assert.Equal(new[] {"video", "audio", "images"}, copy.MediaTypes);
      Assert.Equal("title", copy.SortOrder);
      Assert.Equal("asc", copy.SortDirection);
      Assert.Empty(copy.Views);
      Assert.True(copy.Restrictions.IsEmpty);
    }

    [Fact]
    public void EveryProblemIsReportedTogether()
    {
      var ex = LoadFails(@"{ ""servers"": [
        { ""name"": ""A"", ""folders"": [], ""mediaTypes"": [], ""views"": [""musik""], ""sortOrder"": ""name"",
          ""restrictions"": { ""macs"": [""00:00:00:00""], ""ips"": [""300.1.1.1""] } },
        { ""name"": ""a"", ""folders"": [""/x""], ""mediaTypes"": [""video""] } ] }");

      Assert.Contains("has no folders", ex.Message);
      Assert.Contains("has no mediaTypes", ex.Message);
      Assert.Contains("unknown sortOrder \"name\"", ex.Message);
      Assert.Contains("unknown view \"musik\"", ex.Message);
      Assert.Contains("invalid MAC address \"00:00:00:00\"", ex.Message);
      Assert.Contains("invalid IP address \"300.1.1.1\"", ex.Message);
      Assert.Contains("defined more than once", ex.Message);
      Assert.Contains("sdlna --server config --edit", ex.Message);
    }

    [Theory]
    [InlineData(@"{ ""prot"": 1 }", "unknown setting 'prot' in the top level")]
    [InlineData(@"{ ""servers"": [ { ""name"": ""A"", ""folders"": [""/x""], ""mediatype"": [""video""] } ] }",
      "unknown setting 'mediatype' in a server")]
    [InlineData(@"{ ""servers"": [ { ""name"": ""A"", ""folders"": [""/x""], ""mediaTypes"": [""video""], ""restrictions"": { ""mac"": [] } } ] }",
      "unknown setting 'mac' in a server's restrictions")]
    public void TypoInAKeyIsReportedByName(string json, string expected)
    {
      Assert.Contains(expected, LoadFails(json).Message);
    }

    [Fact]
    public void WrongValueTypeNamesTheKey()
    {
      Assert.Contains("'Port'", LoadFails(@"{ ""port"": ""eighty"" }").Message);
    }

    [Fact]
    public void BrokenJsonReportsWhere()
    {
      var ex = LoadFails(@"{ ""port"": 8200,, }");
      Assert.Contains("is not valid JSON", ex.Message);
      Assert.Contains("BytePositionInLine", ex.Message);
    }

    [Fact]
    public void CommentsTrailingCommasAndSpellingAreForgiven()
    {
      home.WriteConfig(@"{
        // hand-written
        ""port"": 8911,
        ""servers"": [ { ""name"": ""Music"", ""folders"": [""/x""], ""mediaTypes"": [""AUDIO"", ""image""], ""sortDirection"": ""DESC"", }, ],
      }");

      var server = Assert.Single(ConfigurationStore.Load().Servers);

      Assert.Equal(new[] {"audio", "images"}, server.MediaTypes);
      Assert.Equal("desc", server.SortDirection);
      Assert.Equal("title", server.SortOrder);
    }

    [Fact]
    public void CacheDefaultsToTheConfigurationFolderAndCanBeTurnedOff()
    {
      Assert.Equal(ConfigurationStore.DefaultCachePath,
                   ConfigurationStore.ResolveCache(new SdlnaConfiguration()).FullName);
      Assert.Null(ConfigurationStore.ResolveCache(new SdlnaConfiguration {Cache = "none"}));
      Assert.Equal(Path.Combine(home.Path, "media.db"),
                   ConfigurationStore.ResolveCache(new SdlnaConfiguration {Cache = "~/media.db"}).FullName);
    }

    [Fact]
    public void ConfigurationFolderIsCreatedHiddenOnWindows()
    {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "the hidden attribute is Windows-specific");

      ConfigurationStore.EnsureDirectory();

      Assert.True(File.GetAttributes(ConfigurationStore.DirectoryPath).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void FoldersReachedThroughASymlinkAreTheSameFolder()
    {
      var target = home.Folder("media");
      var link = Path.Combine(home.Path, "media-link");
      try {
        Directory.CreateSymbolicLink(link, target);
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
        Assert.Skip($"cannot create symbolic links here: {ex.Message}");
      }

      Assert.Equal(ConfigurationValues.CanonicalFolder(target),
                   ConfigurationValues.CanonicalFolder(link));
    }
  }
}
