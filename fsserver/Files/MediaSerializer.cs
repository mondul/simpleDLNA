using System;
using System.IO;
using System.Text;

namespace NMaier.SimpleDlna.FileMediaServer
{
  /// <summary>
  ///   Binary encoding for the entries held in the file store.
  /// </summary>
  /// <remarks>
  ///   <para>
  ///     Replaces BinaryFormatter, whose implementation was removed from the
  ///     runtime in .NET 9. The format is explicit and self-describing: each
  ///     type reads and writes its own fields in a fixed order, and no CLR
  ///     type or assembly identity is recorded in the blob. That is the point
  ///     -- BinaryFormatter blobs named their types, which is what made them
  ///     a deserialization gadget hazard.
  ///   </para>
  ///   <para>
  ///     Changing any payload layout requires bumping FileStore.SCHEMA, which
  ///     causes existing caches to be discarded and rebuilt.
  ///   </para>
  /// </remarks>
  internal static class MediaSerializer
  {
    private const byte FORMAT_VERSION = 1;

    private const byte KIND_AUDIO = 1;

    private const byte KIND_IMAGE = 2;

    private const byte KIND_VIDEO = 3;

    /// <summary>
    ///   Whether a file has a cacheable representation. Replaces the old
    ///   check for the [Serializable] type attribute.
    /// </summary>
    internal static bool CanSerialize(BaseFile file)
    {
      return file is AudioFile || file is ImageFile || file is VideoFile;
    }

    internal static void Serialize(Stream stream, BaseFile file)
    {
      using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
        writer.Write(FORMAT_VERSION);

        var audio = file as AudioFile;
        if (audio != null) {
          writer.Write(KIND_AUDIO);
          audio.Serialize(writer);
          return;
        }
        var image = file as ImageFile;
        if (image != null) {
          writer.Write(KIND_IMAGE);
          image.Serialize(writer);
          return;
        }
        var video = file as VideoFile;
        if (video != null) {
          writer.Write(KIND_VIDEO);
          video.Serialize(writer);
          return;
        }
        throw new NotSupportedException(
          "Cannot serialize " + file.GetType());
      }
    }

    internal static BaseFile DeserializeFile(Stream stream,
      DeserializeInfo info)
    {
      using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
        CheckVersion(reader);
        var kind = reader.ReadByte();
        switch (kind) {
        case KIND_AUDIO:
          return new AudioFile(reader, info);
        case KIND_IMAGE:
          return new ImageFile(reader, info);
        case KIND_VIDEO:
          return new VideoFile(reader, info);
        default:
          throw new InvalidDataException(
            $"Unknown cache entry kind {kind}");
        }
      }
    }

    internal static void SerializeCover(Stream stream, Cover cover)
    {
      using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
        writer.Write(FORMAT_VERSION);
        cover.Serialize(writer);
      }
    }

    internal static Cover DeserializeCover(Stream stream,
      DeserializeInfo info)
    {
      using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
        CheckVersion(reader);
        return new Cover(reader, info);
      }
    }

    private static void CheckVersion(BinaryReader reader)
    {
      var version = reader.ReadByte();
      if (version != FORMAT_VERSION) {
        throw new InvalidDataException(
          $"Unsupported cache format version {version}");
      }
    }

    internal static void WriteNullable(this BinaryWriter writer, string value)
    {
      writer.Write(value != null);
      if (value != null) {
        writer.Write(value);
      }
    }

    internal static string ReadNullableString(this BinaryReader reader)
    {
      return reader.ReadBoolean() ? reader.ReadString() : null;
    }

    internal static void WriteNullable(this BinaryWriter writer, int? value)
    {
      writer.Write(value.HasValue);
      if (value.HasValue) {
        writer.Write(value.Value);
      }
    }

    internal static int? ReadNullableInt32(this BinaryReader reader)
    {
      return reader.ReadBoolean() ? reader.ReadInt32() : (int?)null;
    }

    internal static void WriteNullable(this BinaryWriter writer, long? value)
    {
      writer.Write(value.HasValue);
      if (value.HasValue) {
        writer.Write(value.Value);
      }
    }

    internal static long? ReadNullableInt64(this BinaryReader reader)
    {
      return reader.ReadBoolean() ? reader.ReadInt64() : (long?)null;
    }

    internal static void WriteStrings(this BinaryWriter writer,
      string[] values)
    {
      if (values == null) {
        writer.Write(-1);
        return;
      }
      writer.Write(values.Length);
      foreach (var value in values) {
        writer.WriteNullable(value);
      }
    }

    internal static string[] ReadStrings(this BinaryReader reader)
    {
      var count = reader.ReadInt32();
      if (count < 0) {
        return null;
      }
      var rv = new string[count];
      for (var i = 0; i < count; ++i) {
        rv[i] = reader.ReadNullableString();
      }
      return rv;
    }

    internal static void WriteBytes(this BinaryWriter writer, byte[] value)
    {
      if (value == null) {
        writer.Write(-1);
        return;
      }
      writer.Write(value.Length);
      writer.Write(value);
    }

    internal static byte[] ReadByteArray(this BinaryReader reader)
    {
      var count = reader.ReadInt32();
      if (count < 0) {
        return null;
      }
      var rv = reader.ReadBytes(count);
      if (rv.Length != count) {
        throw new EndOfStreamException(
          "Truncated blob: expected " + count + " bytes, got " + rv.Length);
      }
      return rv;
    }
  }
}
