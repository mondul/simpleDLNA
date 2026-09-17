using System;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using NMaier.SimpleDlna.Utilities;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   Media durations as clients receive them: H+:MM:SS.FFF, as UPnP
  ///   ContentDirectory requires for res@duration, whatever the culture.
  /// </summary>
  public sealed class DurationTests : IDisposable
  {
    private readonly TempDirectory temp = new TempDirectory();

    private readonly FileServer server;

    public DurationTests()
    {
      server = TestFileServer.Create(temp.Info);
    }

    public void Dispose()
    {
      server.Dispose();
      temp.Dispose();
    }

    public static TheoryData<TimeSpan, string> Durations => new TheoryData<TimeSpan, string>
    {
      {TimeSpan.FromMilliseconds(100), "0:00:00.100"},
      {TimeSpan.FromMilliseconds(2366), "0:00:02.366"},
      {TimeSpan.FromMinutes(97), "1:37:00.000"},
      {new TimeSpan(0, 23, 59, 59, 999), "23:59:59.999"},
      {TimeSpan.FromDays(1), "24:00:00.000"},
      {new TimeSpan(4, 5, 6, 7, 89), "101:06:07.089"},
      // Less than a millisecond is dropped, never rounded up into the seconds.
      {TimeSpan.FromTicks(TimeSpan.TicksPerHour - 1), "0:59:59.999"}
    };

    [Theory]
    [MemberData(nameof(Durations))]
    public void DurationsAreFormattedInHoursWithADecimalPoint(TimeSpan duration, string expected)
    {
      using (CultureScope.CommaDecimals()) {
        Assert.Equal(expected, duration.FormatDuration());
      }
    }

    [Fact]
    public void NegativeDurationsAreRejected()
    {
      Assert.Throws<ArgumentOutOfRangeException>(
        () => TimeSpan.FromSeconds(-1).FormatDuration());
    }

    /// <summary>
    ///   Regression: the Duration property used TimeSpan's "g" format, so on an
    ///   es-CO machine a 2.366 s MP3 was announced as "0:00:02,366", and past
    ///   24 hours (an audiobook) the days came first, as in "1:1:01:02,5".
    /// </summary>
    [Theory]
    [InlineData(2366, "0:00:02.366")]
    [InlineData(90_062_500, "25:01:02.500")]
    public void AudioDurationIsFormattedIndependentlyOfCulture(long milliseconds, string expected)
    {
      var song = new AudioFile(server, temp.WriteFile("song.mp3"), DlnaMime.AudioMP3)
        .Set("duration", (TimeSpan?)TimeSpan.FromMilliseconds(milliseconds))
        .Set("initialized", true);

      using (CultureScope.CommaDecimals()) {
        Assert.Equal(expected, song.Properties["Duration"]);
      }
    }

    /// <summary>
    ///   Regression: as for audio, "g" gave "0:00:02,366" on an es-CO machine
    ///   and "1:1:01:02,5" for a video longer than a day.
    /// </summary>
    [Theory]
    [InlineData(2366, "0:00:02.366")]
    [InlineData(90_062_500, "25:01:02.500")]
    public void VideoDurationIsFormattedIndependentlyOfCulture(long milliseconds, string expected)
    {
      var video = new VideoFile(server, temp.WriteFile("movie.mkv"), DlnaMime.VideoMKV)
        .Set("duration", (TimeSpan?)TimeSpan.FromMilliseconds(milliseconds))
        .Set("subTitle", new Subtitle())
        .Set("initialized", true);

      using (CultureScope.CommaDecimals()) {
        Assert.Equal(expected, video.Properties["Duration"]);
      }
    }

    [Fact]
    public void FilesWithoutADurationHaveNoDurationProperty()
    {
      var song = new AudioFile(server, temp.WriteFile("song.mp3"), DlnaMime.AudioMP3)
        .Set("initialized", true);

      Assert.False(song.Properties.ContainsKey("Duration"));
    }
  }
}
