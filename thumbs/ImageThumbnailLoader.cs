using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using SixLabors.ImageSharp;

namespace NMaier.SimpleDlna.Thumbnails
{
  internal sealed class ImageThumbnailLoader : IThumbnailLoader
  {
    public DlnaMediaTypes Handling => DlnaMediaTypes.Image;

    public MemoryStream GetThumbnail(object item, ref int width,
      ref int height)
    {
      Image img;
      Size fit;
      var stream = item as Stream;
      if (stream != null) {
        img = ThumbnailMaker.LoadImage(stream, width, height, out fit);
      }
      else {
        var fi = item as FileInfo;
        if (fi == null) {
          throw new NotSupportedException();
        }
        using (var file = fi.OpenRead()) {
          img = ThumbnailMaker.LoadImage(file, width, height, out fit);
        }
      }
      using (img) {
        return ThumbnailMaker.ResizeToJpeg(
          img, fit, ref width, ref height, ThumbnailMakerBorder.Borderless);
      }
    }
  }
}
