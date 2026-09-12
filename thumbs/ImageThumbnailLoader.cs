using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using SkiaSharp;

namespace NMaier.SimpleDlna.Thumbnails
{
  internal sealed class ImageThumbnailLoader : IThumbnailLoader
  {
    public DlnaMediaTypes Handling => DlnaMediaTypes.Image;

    public MemoryStream GetThumbnail(object item, ref int width,
      ref int height)
    {
      SKBitmap img;
      var stream = item as Stream;
      if (stream != null) {
        img = SKBitmap.Decode(stream);
      }
      else {
        var fi = item as FileInfo;
        if (fi != null) {
          img = SKBitmap.Decode(fi.FullName);
        }
        else {
          throw new NotSupportedException();
        }
      }
      // Unlike Image.FromStream, SKBitmap.Decode reports failure by returning
      // null rather than throwing.
      if (img == null) {
        throw new NotSupportedException("Not a supported image format");
      }
      using (img) {
        return ThumbnailMaker.ResizeToJpeg(
          img, ref width, ref height, ThumbnailMakerBorder.Borderless);
      }
    }
  }
}
