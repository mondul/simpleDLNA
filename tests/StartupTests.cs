using System;
using System.IO;
using System.Linq;
using NMaier.SimpleDlna.Tests.Support;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   What "sdlna" does when run without folders.
  /// </summary>
  [Collection(ProcessStateCollection.NAME)]
  public sealed class StartupTests : IDisposable
  {
    private readonly ConfigurationHome home = new ConfigurationHome();

    public void Dispose()
    {
      home.Dispose();
    }

    private static Options Parse(params string[] args)
    {
      var options = new Options();
      options.Parse(args);
      return options;
    }

    [Fact]
    public void WithoutAConfigurationTheCurrentDirectoryIsServed()
    {
      var options = Parse();
      var warnings = new StringWriter();

      var config = Program.ChooseConfiguredServers(options, warnings);

      Assert.Null(config);
      var directory = Assert.Single(options.Directories);
      Assert.Equal(Path.GetFullPath("."), Path.TrimEndingDirectorySeparator(directory.FullName));
      Assert.Contains("No servers are configured and no configuration file exists", warnings.ToString());
      Assert.Contains("sdlna --server add <name> <folder>", warnings.ToString());
    }

    [Fact]
    public void AConfigurationWithoutServersAlsoServesTheCurrentDirectory()
    {
      home.WriteConfig(@"{ ""port"": 0, ""servers"": [] }");
      var options = Parse();
      var warnings = new StringWriter();

      Assert.Null(Program.ChooseConfiguredServers(options, warnings));

      Assert.Single(options.Directories);
      Assert.Contains($"No servers are configured in {home.ConfigPath}", warnings.ToString());
    }

    [Fact]
    public void ConfiguredServersAreChosenWithoutAWarning()
    {
      home.WriteConfig($@"{{ ""servers"": [ {{ ""name"": ""Movies"", ""folders"": [""{home.Folder("movies").Replace("\\", "\\\\")}""], ""mediaTypes"": [""video""] }} ] }}");
      var options = Parse();
      var warnings = new StringWriter();

      var config = Program.ChooseConfiguredServers(options, warnings);

      Assert.Equal("Movies", Assert.Single(config.Servers).Name);
      Assert.Empty(options.Directories);
      Assert.Equal("", warnings.ToString());
    }

    [Fact]
    public void AnInvalidConfigurationIsAnErrorRatherThanAFallback()
    {
      home.WriteConfig(@"{ ""port"": ""oops"" }");
      var options = Parse();

      Assert.Throws<ConfigurationException>(
        () => Program.ChooseConfiguredServers(options, new StringWriter()));
      Assert.Empty(options.Directories);
    }

    [Fact]
    public void PerServerOptionsAreRecognisedAndProcessWideOnesAreNot()
    {
      Assert.Equal(new[] {"-t", "-v", "-d", "-i"},
                   Parse("-t", "video", "-v", "music", "-d", "-i", "127.0.0.1").GivenServerOptions().ToArray());
      Assert.Empty(Parse("-p", "8200", "-l", "DEBUG", "--no-rescanning").GivenServerOptions());
    }

    [Fact]
    public void ExplicitPortZeroStillCountsAsGiven()
    {
      Assert.True(Parse("-p", "0").PortSpecified);
      Assert.False(Parse().PortSpecified);
    }
  }
}
