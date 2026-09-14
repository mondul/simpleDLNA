using System;
using System.IO;
using System.Linq;
using NMaier.SimpleDlna.Tests.Support;
using NMaier.SimpleDlna.Utilities;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   Environment.GetFolderPath returns "" for a folder that does not exist.
  ///   These tests point HOME at a missing directory, which only redirects the
  ///   home folder on Linux and macOS.
  /// </summary>
  [Collection(ProcessStateCollection.NAME)]
  public sealed class HomeDirectoryTests
  {
    private static void WithMissingHome(Action<string> test)
    {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "HOME does not redirect the profile folder on Windows");
      var original = Environment.GetEnvironmentVariable("HOME");
      var missing = Path.Combine(Path.GetTempPath(), "sdlna-tests", "missing-home-" + Guid.NewGuid().ToString("N"));
      Environment.SetEnvironmentVariable("HOME", missing);
      try {
        Assert.Equal("", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        test(missing);
      }
      finally {
        Environment.SetEnvironmentVariable("HOME", original);
      }
    }

    /// <summary>
    ///   Regression: new DirectoryInfo("") threw inside FFmpeg's static
    ///   initializer, disabling video thumbnails and durations.
    /// </summary>
    [Fact]
    public void FFmpegSearchLocationsTolerateAMissingHome()
    {
      WithMissingHome(missing =>
      {
        var locations = FFmpeg.GetSpecialLocations();
        Assert.DoesNotContain(locations, l => string.IsNullOrEmpty(l.FullName));
        Assert.DoesNotContain(locations, l => l.FullName.StartsWith(missing, StringComparison.Ordinal));
      });
    }

    /// <summary>
    ///   Regression: the configuration path became ".sdlna/config.json",
    ///   relative to wherever sdlna was run.
    /// </summary>
    [Fact]
    public void ConfigurationStaysUnderAMissingHome()
    {
      WithMissingHome(missing =>
      {
        Assert.Equal(Path.Combine(missing, ".sdlna", "config.json"), ConfigurationStore.FilePath);
      });
    }
  }
}
