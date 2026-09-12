using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;
using SkiaSharp;

namespace NMaier.SimpleDlna.Thumbnails
{
  public sealed class ThumbnailMaker : Logging
  {
    private const int JPEG_QUALITY = 85;

    private static readonly LeastRecentlyUsedDictionary<string, CacheItem> cache =
      new LeastRecentlyUsedDictionary<string, CacheItem>(1 << 11);

    private static readonly Dictionary<DlnaMediaTypes, List<IThumbnailLoader>> thumbers =
      BuildThumbnailers();

    private static Dictionary<DlnaMediaTypes, List<IThumbnailLoader>> BuildThumbnailers()
    {
      var types = Enum.GetValues(typeof (DlnaMediaTypes));
      var buildThumbnailers = types.Cast<DlnaMediaTypes>().ToDictionary(i => i, i => new List<IThumbnailLoader>());
      var a = Assembly.GetExecutingAssembly();
      foreach (var t in a.GetTypes()) {
        if (t.GetInterface("IThumbnailLoader") == null) {
          continue;
        }
        var ctor = t.GetConstructor(new Type[] {});
        var thumber = ctor?.Invoke(new object[] {}) as IThumbnailLoader;
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

    internal static SKBitmap ResizeImage(SKBitmap image, int width, int height,
      ThumbnailMakerBorder border)
    {
      var nw = (float)image.Width;
      var nh = (float)image.Height;
      if (nw > width) {
        nh = width * nh / nw;
        nw = width;
      }
      if (nh > height) {
        nw = height * nw / nh;
        nh = height;
      }

      // A source with an extreme aspect ratio can scale to zero on one axis,
      // which is not a valid bitmap size.
      var rw = border == ThumbnailMakerBorder.Bordered
        ? width
        : Math.Max((int)nw, 1);
      var rh = border == ThumbnailMakerBorder.Bordered
        ? height
        : Math.Max((int)nh, 1);

      var result = new SKBitmap(rw, rh, SKColorType.Rgba8888, SKAlphaType.Premul);
      try {
        // Mitchell cubic when enlarging, cheap linear filtering when shrinking,
        // matching the quality/speed split the GDI+ implementation used.
        var sampling = rw > image.Width && rh > image.Height
          ? new SKSamplingOptions(SKCubicResampler.Mitchell)
          : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using (var canvas = new SKCanvas(result)) {
          canvas.Clear(SKColors.Black);
          using (var img = SKImage.FromBitmap(image)) {
            var rect = SKRect.Create(
              (rw - nw) / 2f, (rh - nh) / 2f, nw, nh);
            canvas.DrawImage(img, rect, sampling);
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
    ///   Scales <paramref name="image" /> to fit and encodes the result as
    ///   JPEG, reporting the dimensions actually produced.
    /// </summary>
    internal static MemoryStream ResizeToJpeg(SKBitmap image, ref int width,
      ref int height, ThumbnailMakerBorder border)
    {
      using (var scaled = ResizeImage(image, width, height, border)) {
        width = scaled.Width;
        height = scaled.Height;
        var rv = new MemoryStream();
        try {
          using (var img = SKImage.FromBitmap(scaled)) {
            using (var data = img.Encode(
              SKEncodedImageFormat.Jpeg, JPEG_QUALITY)) {
              if (data == null) {
                throw new NotSupportedException(
                  "Failed to encode the thumbnail as JPEG");
              }
              data.SaveTo(rv);
            }
          }
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
