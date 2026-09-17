using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using log4net;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NMaier.SimpleDlna.Thumbnails
{
  public sealed class ThumbnailMaker : Logging
  {
    private const int JPEG_QUALITY = 85;

    private static readonly JpegEncoder jpegEncoder = new JpegEncoder
    {
      Quality = JPEG_QUALITY
    };

    /// <summary>
    ///   Only the first frame of an animated image is ever shown, so the rest
    ///   are not decoded.
    /// </summary>
    private static readonly DecoderOptions decoderOptions = new DecoderOptions
    {
      MaxFrames = 1
    };

    private static readonly LeastRecentlyUsedDictionary<string, CacheItem> cache =
      new LeastRecentlyUsedDictionary<string, CacheItem>(1 << 11);

    private static readonly Dictionary<DlnaMediaTypes, List<IThumbnailLoader>> thumbers =
      BuildThumbnailers(Assembly.GetExecutingAssembly().GetTypes());

    /// <summary>
    ///   Instantiates every <see cref="IThumbnailLoader" /> among
    ///   <paramref name="candidates" />, grouped by the media types it handles.
    /// </summary>
    /// <remarks>
    ///   A loader whose constructor throws is skipped. The video loader throws
    ///   when ffmpeg is not installed, and this runs in the static initializer:
    ///   letting that escape made ThumbnailMaker unusable, which took image
    ///   thumbnails and album art down with it on any machine without ffmpeg.
    /// </remarks>
    internal static Dictionary<DlnaMediaTypes, List<IThumbnailLoader>> BuildThumbnailers(
      IEnumerable<Type> candidates)
    {
      var types = Enum.GetValues(typeof (DlnaMediaTypes));
      var buildThumbnailers = types.Cast<DlnaMediaTypes>().ToDictionary(i => i, i => new List<IThumbnailLoader>());
      foreach (var t in candidates) {
        if (t.GetInterface("IThumbnailLoader") == null) {
          continue;
        }
        var ctor = t.GetConstructor(new Type[] {});
        IThumbnailLoader thumber;
        try {
          thumber = ctor?.Invoke(new object[] {}) as IThumbnailLoader;
        }
        catch (TargetInvocationException ex) {
          LogManager.GetLogger(typeof (ThumbnailMaker)).InfoFormat(
            "{0} is unavailable: {1}", t.Name, ex.InnerException?.Message ?? ex.Message);
          continue;
        }
        if (thumber == null) {
          continue;
        }
        foreach (DlnaMediaTypes i in types) {
          if (thumber.Handling.HasFlag(i)) {
            buildThumbnailers[i].Add(thumber);
          }
        }
      }
      return buildThumbnailers;
    }

    private static bool GetThumbnailFromCache(ref string key, ref int width,
      ref int height, out byte[] rv)
    {
      key = $"{width}x{height} {key}";
      lock (cache) {
        CacheItem ci;
        if (cache.TryGetValue(key, out ci)) {
          rv = ci.Data;
          width = ci.Width;
          height = ci.Height;
          return true;
        }
      }
      rv = null;
      return false;
    }

    private byte[] GetThumbnailInternal(string key, object item,
      DlnaMediaTypes type, ref int width,
      ref int height)
    {
      var thumbnailers = thumbers[type];
      var rw = width;
      var rh = height;
      foreach (var thumber in thumbnailers) {
        try {
          using (var i = thumber.GetThumbnail(item, ref width, ref height)) {
            var rv = i.ToArray();
            lock (cache) {
              cache[key] = new CacheItem(rv, rw, rh);
            }
            return rv;
          }
        }
        catch (Exception ex) {
          Debug($"{thumber.GetType()} failed to thumbnail a resource", ex);
        }
      }
      throw new ArgumentException("Not a supported resource");
    }

    /// <summary>
    ///   The size a <paramref name="sourceWidth" /> by
    ///   <paramref name="sourceHeight" /> image is shown at to fit within
    ///   <paramref name="width" /> by <paramref name="height" />, keeping its
    ///   aspect ratio and never enlarging it.
    /// </summary>
    internal static Size FitWithin(int sourceWidth, int sourceHeight,
      int width, int height)
    {
      var nw = (float)sourceWidth;
      var nh = (float)sourceHeight;
      if (nw > width) {
        nh = width * nh / nw;
        nw = width;
      }
      if (nh > height) {
        nw = height * nw / nh;
        nh = height;
      }
      // A source with an extreme aspect ratio can scale to zero on one axis,
      // which is not a valid image size.
      return new Size(Math.Max((int)nw, 1), Math.Max((int)nh, 1));
    }

    /// <summary>
    ///   Decodes the first frame of an image, in any format ImageSharp reads,
    ///   for a thumbnail of at most <paramref name="width" /> by
    ///   <paramref name="height" />.
    /// </summary>
    /// <param name="fit">
    ///   The size to show the image at; see <see cref="FitWithin" />.
    /// </param>
    /// <exception cref="NotSupportedException">
    ///   The data is not an image, or is damaged.
    /// </exception>
    internal static Image LoadImage(Stream stream, int width, int height,
      out Size fit)
    {
      try {
        if (!stream.CanSeek) {
          var whole = Image.Load(decoderOptions, stream);
          fit = FitWithin(whole.Width, whole.Height, width, height);
          return whole;
        }
        var start = stream.Position;
        var info = Image.Identify(decoderOptions, stream);
        stream.Position = start;
        fit = FitWithin(info.Width, info.Height, width, height);
        var options = decoderOptions;
        if (fit.Width < info.Width || fit.Height < info.Height) {
          // JPEGs are then decoded at a reduced scale, which makes a photo's
          // thumbnail several times faster. TargetSize would also enlarge a
          // smaller image, hence only when shrinking.
          options = new DecoderOptions
          {
            MaxFrames = decoderOptions.MaxFrames,
            TargetSize = fit
          };
        }
        return Image.Load(options, stream);
      }
      catch (Exception ex) when (
        ex is ImageFormatException || ex is InvalidImageContentException) {
        throw new NotSupportedException("Not a supported image", ex);
      }
    }

    /// <summary>
    ///   Scales <paramref name="image" /> to <paramref name="fit" /> and
    ///   flattens it onto black. Bordered results are letterboxed to exactly
    ///   <paramref name="width" /> by <paramref name="height" />.
    /// </summary>
    internal static Image<Rgb24> ResizeImage(Image image, Size fit, int width,
      int height, ThumbnailMakerBorder border)
    {
      var rw = border == ThumbnailMakerBorder.Bordered ? width : fit.Width;
      var rh = border == ThumbnailMakerBorder.Bordered ? height : fit.Height;
      var at = new Point((rw - fit.Width) / 2, (rh - fit.Height) / 2);

      // Drawing onto an opaque black canvas, rather than encoding the scaled
      // image directly, gives transparent areas a defined colour: JPEG has no
      // alpha channel.
      var result = new Image<Rgb24>(rw, rh, new Rgb24(0, 0, 0));
      try {
        if (image.Size == fit) {
          result.Mutate(c => c.DrawImage(image, at, 1f));
        }
        else {
          using (var scaled = image.Clone(
            c => c.Resize(fit.Width, fit.Height, KnownResamplers.Bicubic))) {
            result.Mutate(c => c.DrawImage(scaled, at, 1f));
          }
        }
        return result;
      }
      catch (Exception) {
        result.Dispose();
        throw;
      }
    }

    /// <summary>
    ///   Scales <paramref name="image" /> to <paramref name="fit" /> and
    ///   encodes the result as JPEG, reporting the dimensions actually
    ///   produced.
    /// </summary>
    internal static MemoryStream ResizeToJpeg(Image image, Size fit,
      ref int width, ref int height, ThumbnailMakerBorder border)
    {
      using (var scaled = ResizeImage(image, fit, width, height, border)) {
        width = scaled.Width;
        height = scaled.Height;
        var rv = new MemoryStream();
        try {
          scaled.SaveAsJpeg(rv, jpegEncoder);
          return rv;
        }
        catch (Exception) {
          rv.Dispose();
          throw;
        }
      }
    }

    public IThumbnail GetThumbnail(FileSystemInfo file, int width, int height)
    {
      if (file == null) {
        throw new ArgumentNullException(nameof(file));
      }
      var ext = file.Extension.ToUpperInvariant().Substring(1);
      var mediaType = DlnaMaps.Ext2Media[ext];

      var key = file.FullName;
      byte[] rv;
      if (GetThumbnailFromCache(ref key, ref width, ref height, out rv)) {
        return new Thumbnail(width, height, rv);
      }

      rv = GetThumbnailInternal(key, file, mediaType, ref width, ref height);
      return new Thumbnail(width, height, rv);
    }

    public IThumbnail GetThumbnail(string key, DlnaMediaTypes type,
      Stream stream, int width, int height)
    {
      byte[] rv;
      if (GetThumbnailFromCache(ref key, ref width, ref height, out rv)) {
        return new Thumbnail(width, height, rv);
      }
      rv = GetThumbnailInternal(key, stream, type, ref width, ref height);
      return new Thumbnail(width, height, rv);
    }

    private struct CacheItem
    {
      public readonly byte[] Data;

      public readonly int Height;

      public readonly int Width;

      public CacheItem(byte[] aData, int aWidth, int aHeight)
      {
        Data = aData;
        Width = aWidth;
        Height = aHeight;
      }
    }
  }
}
