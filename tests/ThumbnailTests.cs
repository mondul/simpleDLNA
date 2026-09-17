using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Tests.Support;
using NMaier.SimpleDlna.Thumbnails;
using SixLabors.ImageSharp.PixelFormats;
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
      using (var decoded = TestMedia.Decode(data)) {
        Assert.Equal(288, decoded.Width);
        Assert.Equal(216, decoded.Height);
        AssertClose(TestMedia.SeaGreen, decoded[144, 108]);
      }
    }

    private static void AssertClose(Rgb24 expected, Rgb24 actual)
    {
      // JPEG is lossy; a few steps either way is the same colour.
      Assert.True(
        Math.Abs(expected.R - actual.R) <= 12 &&
        Math.Abs(expected.G - actual.G) <= 12 &&
        Math.Abs(expected.B - actual.B) <= 12,
        $"expected about {expected}, got {actual}");
    }

    [Fact]
    public void SmallImagesAreNotEnlarged()
    {
      var source = TestMedia.WriteJpeg(temp.Combine("icon.jpg"), 100, 50);

      var thumbnail = new ThumbnailMaker().GetThumbnail(source, 384, 216);

      Assert.Equal(100, thumbnail.Width);
      Assert.Equal(50, thumbnail.Height);
      using (var decoded = TestMedia.Decode(thumbnail.GetData())) {
        Assert.Equal(100, decoded.Width);
      }
    }

    /// <summary>
    ///   JPEG has no alpha channel, so transparent areas are flattened onto
    ///   black, as the Skia and GDI+ implementations did, whatever colour the
    ///   transparent pixels hold.
    /// </summary>
    [Fact]
    public void TransparencyBecomesBlack()
    {
      var source = TestMedia.WriteHalfTransparentPng(temp.Combine("logo.png"), 200, 100);

      var thumbnail = new ThumbnailMaker().GetThumbnail(source, 100, 100);

      using (var decoded = TestMedia.Decode(thumbnail.GetData())) {
        Assert.Equal(100, decoded.Width);
        Assert.Equal(50, decoded.Height);
        AssertClose(new Rgb24(255, 255, 255), decoded[10, 25]);
        AssertClose(new Rgb24(0, 0, 0), decoded[90, 25]);
      }
    }

    [Fact]
    public void AnimatedGifShowsItsFirstFrame()
    {
      var source = TestMedia.WriteAnimatedGif(
        temp.Combine("anim.gif"), 64, 64,
        new Rgba32(255, 0, 0), new Rgba32(0, 0, 255));

      var thumbnail = new ThumbnailMaker().GetThumbnail(source, 32, 32);

      using (var decoded = TestMedia.Decode(thumbnail.GetData())) {
        AssertClose(new Rgb24(255, 0, 0), decoded[16, 16]);
      }
    }

    [Fact]
    public void DamagedImageIsReportedAsUnsupported()
    {
      var source = temp.WriteFile("broken.jpg", 512);

      Assert.Throws<ArgumentException>(
        () => new ThumbnailMaker().GetThumbnail(source, 384, 216));
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
