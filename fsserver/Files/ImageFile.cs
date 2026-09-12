using System;
using System.IO;
using NMaier.SimpleDlna.Server;
using TagLib;
using File = TagLib.File;

namespace NMaier.SimpleDlna.FileMediaServer
{
  internal sealed class ImageFile :
    BaseFile, IMediaImageResource
  {
    private string creator;

    private string description;

    private bool initialized;

    private string title;

    private int? width,
      height;

    internal ImageFile(BinaryReader reader, DeserializeInfo di)
      : this(di.Server, di.Info, di.Type)
    {
      creator = reader.ReadNullableString();
      description = reader.ReadNullableString();
      title = reader.ReadNullableString();
      width = reader.ReadNullableInt32();
      height = reader.ReadNullableInt32();

      initialized = true;
    }

    internal ImageFile(FileServer server, FileInfo aFile, DlnaMime aType)
      : base(server, aFile, aType, DlnaMediaTypes.Image)
    {
    }

    public string MetaCreator
    {
      get {
        MaybeInit();
        return creator;
      }
    }

    public string MetaDescription
    {
      get {
        MaybeInit();
        return description;
      }
    }

    public int? MetaHeight
    {
      get {
        MaybeInit();
        return height;
      }
    }

    public int? MetaWidth
    {
      get {
        MaybeInit();
        return width;
      }
    }

    public override IHeaders Properties
    {
      get {
        MaybeInit();
        var rv = base.Properties;
        if (description != null) {
          rv.Add("Description", description);
        }
        if (creator != null) {
          rv.Add("Creator", creator);
        }
        if (width != null && height != null) {
          rv.Add(
            "Resolution",
            $"{width.Value}x{height.Value}"
            );
        }
        return rv;
      }
    }

    public override string Title
    {
      get {
        if (!string.IsNullOrWhiteSpace(title)) {
          return $"{base.Title} — {title}";
        }
        return base.Title;
      }
    }

    internal void Serialize(BinaryWriter writer)
    {
      MaybeInit();
      writer.WriteNullable(creator);
      writer.WriteNullable(description);
      writer.WriteNullable(title);
      writer.WriteNullable(width);
      writer.WriteNullable(height);
    }

    private void MaybeInit()
    {
      if (initialized) {
        return;
      }

      try {
        using (var tl = File.Create(new TagLibFileAbstraction(Item))) {
          try {
            width = tl.Properties.PhotoWidth;
            height = tl.Properties.PhotoHeight;
          }
          catch (Exception ex) {
            Debug("Failed to transpose Properties props", ex);
          }

          try {
            var t = ((TagLib.Image.File)tl).ImageTag;
            title = t.Title;
            if (string.IsNullOrWhiteSpace(title)) {
              title = null;
            }
            description = t.Comment;
            if (string.IsNullOrWhiteSpace(description)) {
              description = null;
            }
            creator = t.Creator;
            if (string.IsNullOrWhiteSpace(creator)) {
              creator = null;
            }
          }
          catch (Exception ex) {
            Debug("Failed to transpose Tag props", ex);
          }
        }


        initialized = true;

        Server.UpdateFileCache(this);
      }
      catch (CorruptFileException ex) {
        Debug(
          "Failed to read meta data via taglib for file " + Item.FullName, ex);
        initialized = true;
      }
      catch (UnsupportedFormatException ex) {
        Debug(
          "Failed to read meta data via taglib for file " + Item.FullName, ex);
        initialized = true;
      }
      catch (Exception ex) {
        Warn(
          "Unhandled exception reading meta data for file " + Item.FullName,
          ex);
      }
    }
  }
}
