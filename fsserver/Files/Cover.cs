using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Metadata;
using NMaier.SimpleDlna.Thumbnails;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer
{
  internal sealed class Cover
    : Logging, IMediaCoverResource, IMetaInfo, IDisposable
  {
    private static readonly ThumbnailMaker thumber =
      new ThumbnailMaker();

    private readonly FileInfo file;
    private byte[] bytes;

    private int height = 216;

    private bool warned;

    private int width = 384;

    internal Cover(BinaryReader reader, DeserializeInfo di)
    {
      width = reader.ReadInt32();
      height = reader.ReadInt32();
      bytes = reader.ReadByteArray();
      if (di != null) {
        file = di.Info;
      }
    }

    internal Cover(FileInfo aFile, Stream aStream)
    {
      var thumb = thumber.GetThumbnail(
        aFile.FullName,
        DlnaMediaTypes.Image,
        aStream,
        width,
        height
        );
      bytes = thumb.GetData();
      height = thumb.Height;
      width = thumb.Width;
    }

    public Cover(FileInfo aFile)
    {
      file = aFile;
    }

    private byte[] Bytes
    {
      get {
        var rv = ForceLoad();
        if (rv == null || rv.Length == 0) {
          throw new NotSupportedException();
        }
        return rv;
      }
    }

    IMediaCoverResource IMediaCover.Cover => this;

    public string Id
    {
      get { throw new NotSupportedException(); }
      set { throw new NotSupportedException(); }
    }

    public DlnaMediaTypes MediaType => DlnaMediaTypes.Image;

    public int? MetaHeight => height;

    public int? MetaWidth => width;

    public string Path
    {
      get { throw new NotSupportedException(); }
    }

    public string PN => "JPEG_TN";

    public IHeaders Properties
    {
      get { throw new NotSupportedException(); }
    }

    public string Title
    {
      get { throw new NotSupportedException(); }
    }

    public DlnaMime Type => DlnaMime.ImageJPEG;

    public int CompareTo(IMediaItem other)
    {
      throw new NotSupportedException();
    }

    public Stream CreateContentStream()
    {
      return new MemoryStream(Bytes);
    }

    public bool Equals(IMediaItem other)
    {
      throw new NotImplementedException();
    }

    public string ToComparableTitle()
    {
      throw new NotImplementedException();
    }

    public DateTime InfoDate
    {
      get {
        if (file != null) {
          return file.LastWriteTimeUtc;
        }
        return DateTime.Now;
      }
    }

    public long? InfoSize
    {
      get {
        try {
          var b = Bytes;
          return b?.Length;
        }
        catch (NotSupportedException) {
          return null;
        }
      }
    }

    internal void Serialize(BinaryWriter writer)
    {
      if (bytes == null) {
        throw new NotSupportedException("No cover loaded");
      }
      writer.Write(width);
      writer.Write(height);
      writer.WriteBytes(bytes);
    }

    internal event EventHandler OnCoverLazyLoaded;

    internal byte[] ForceLoad()
    {
      try {
        if (bytes == null) {
          var thumb = thumber.GetThumbnail(
            file,
            width,
            height
            );
          bytes = thumb.GetData();
          height = thumb.Height;
          width = thumb.Width;
        }
      }
      catch (NotSupportedException ex) {
        Debug("Failed to load thumb for " + file.FullName, ex);
      }
      catch (Exception ex) {
        if (!warned) {
          Warn("Failed to load thumb for " + file.FullName, ex);
          warned = true;
        }
        else {
          Debug("Failed to load thumb for " + file.FullName, ex);
        }
        return null;
      }
      if (bytes == null) {
        bytes = new byte[0];
      }
      OnCoverLazyLoaded?.Invoke(this, null);
      return bytes;
    }

    public void Dispose()
    {
      bytes = null;
    }
  }
}
