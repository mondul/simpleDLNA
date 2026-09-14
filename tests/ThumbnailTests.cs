using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using NMaier.SimpleDlna.Thumbnails;
using SkiaSharp;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  public sealed class ThumbnailTests : IDisposable
  {
    private readonly TempDirectory temp = new TempDirectory();

    public void Dispose()
    {
      temp.Dispose();
    }

    [Fact]
    public void ImageThumbnailKeepsTheAspectRatioAndIsAJpeg()
    {
      var source = TestMedia.WriteJpeg(temp.Combine("photo.jpg"), 800, 600);

      var thumbnail = new ThumbnailMaker().GetThumbnail(source, 384, 216);

      Assert.Equal(288, thumbnail.Width);
      Assert.Equal(216, thumbnail.Height);
      var data = thumbnail.GetData();
      Assert.True(TestMedia.IsJpeg(data));
      using (var decoded = SKBitmap.Decode(data)) {
        Assert.Equal(288, decoded.Width);
        Assert.Equal(216, decoded.Height);
      }
    }

    [Fact]
    public void ExtremeAspectRatioDoesNotProduceAnEmptyBitmap()
    {
      var source = TestMedia.WriteJpeg(temp.Combine("strip.jpg"), 4000, 2);

      var thumbnail = new ThumbnailMaker().GetThumbnail(source, 384, 216);

      Assert.True(thumbnail.Height >= 1);
      Assert.True(TestMedia.IsJpeg(thumbnail.GetData()));
    }

    [Fact]
    public void VideoThumbnailComesFromFFmpeg()
    {
      Assert.SkipUnless(TestMedia.HasFFmpeg, "ffmpeg is not installed");
      var video = TestMedia.WriteVideo(temp.Combine("clip.mp4"));

      var thumbnail = new ThumbnailMaker().GetThumbnail(video, 384, 216);

      // Video thumbnails are letterboxed to the requested size.
      Assert.Equal(384, thumbnail.Width);
      Assert.Equal(216, thumbnail.Height);
      Assert.True(TestMedia.IsJpeg(thumbnail.GetData()));
    }

    private sealed class UnavailableLoader : IThumbnailLoader
    {
      public UnavailableLoader()
      {
        throw new NotSupportedException("dependency missing");
      }

      public DlnaMediaTypes Handling => DlnaMediaTypes.Video;

      public MemoryStream GetThumbnail(object item, ref int width, ref int height)
      {
        throw new NotImplementedException();
      }
    }

    private sealed class WorkingLoader : IThumbnailLoader
    {
      public DlnaMediaTypes Handling => DlnaMediaTypes.Image;

      public MemoryStream GetThumbnail(object item, ref int width, ref int height)
      {
        throw new NotImplementedException();
      }
    }

    /// <summary>
    ///   Regression: the video loader throws without ffmpeg, and that used to
    ///   escape ThumbnailMaker's static initializer, disabling image thumbnails
    ///   and album art as well.
    /// </summary>
    [Fact]
    public void LoaderThatCannotBeCreatedIsSkippedWithoutAffectingOthers()
    {
      var loaders = ThumbnailMaker.BuildThumbnailers(
        new[] {typeof (UnavailableLoader), typeof (WorkingLoader)});

      Assert.Empty(loaders[DlnaMediaTypes.Video]);
      Assert.IsType<WorkingLoader>(Assert.Single(loaders[DlnaMediaTypes.Image]));
    }
  }
}
