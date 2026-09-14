using System.Linq;
using NMaier.SimpleDlna.Server;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  /// <summary>
  ///   Which MIME types clients are told, including the Samsung exception.
  /// </summary>
  public sealed class MimeTypeTests
  {
    private const string GENERIC = "VLC/3.0.20 LibVLC/3.0.20";

    private static RawHeaders Client(string userAgent)
    {
      return new RawHeaders {{"User-Agent", userAgent}};
    }

    [Theory]
    [InlineData("MKV", DlnaMime.VideoMKV)]
    [InlineData("MK3D", DlnaMime.VideoMKV)]
    [InlineData("WEBM", DlnaMime.VideoWEBM)]
    [InlineData("MP4", DlnaMime.VideoAVC)]
    public void ExtensionsMapToTypes(string extension, DlnaMime expected)
    {
      Assert.Equal(expected, DlnaMaps.Ext2Dlna[extension]);
      Assert.Equal(DlnaMediaTypes.Video, DlnaMaps.Ext2Media[extension]);
    }

    [Theory]
    [InlineData(DlnaMime.VideoMKV, "video/x-matroska")]
    [InlineData(DlnaMime.VideoWEBM, "video/webm")]
    [InlineData(DlnaMime.VideoAVC, "video/mp4")]
    public void GenericClientsGetStandardTypes(DlnaMime type, string expected)
    {
      Assert.Equal(expected, DlnaMaps.MimeFor(type, Client(GENERIC)));
      Assert.Equal(expected, DlnaMaps.MimeFor(type, null));
    }

    [Theory]
    [InlineData("DLNADOC/1.50 SEC_HHP_[TV] Samsung Q70 Series (55)/1.0 UPnP/1.0")]
    [InlineData("SamsungWiselinkPro/1.0")]
    public void SamsungClientsKeepVideoXMkvForMatroskaAndWebM(string userAgent)
    {
      Assert.Equal("video/x-mkv", DlnaMaps.MimeFor(DlnaMime.VideoMKV, Client(userAgent)));
      Assert.Equal("video/x-mkv", DlnaMaps.MimeFor(DlnaMime.VideoWEBM, Client(userAgent)));
      Assert.Equal("video/mp4", DlnaMaps.MimeFor(DlnaMime.VideoAVC, Client(userAgent)));
    }

    [Fact]
    public void ProtocolInfoMatchesTheTypesEachClientIsTold()
    {
      var generic = DlnaMaps.ProtocolInfoFor(Client(GENERIC)).Split(',');
      var samsung = DlnaMaps.ProtocolInfoFor(Client("SEC_HHP_[TV] Samsung")).Split(',');

      Assert.Contains(generic, e => e.Contains(":video/x-matroska:"));
      Assert.Contains(generic, e => e.Contains(":video/webm:"));
      Assert.DoesNotContain(generic, e => e.Contains(":video/x-mkv:"));

      Assert.Contains(samsung, e => e.Contains(":video/x-mkv:"));
      Assert.DoesNotContain(samsung, e => e.Contains(":video/x-matroska:") || e.Contains(":video/webm:"));

      // Matroska and WebM share both MIME and profile for Samsung.
      Assert.Equal(generic.Length, generic.Distinct().Count());
      Assert.Equal(samsung.Length, samsung.Distinct().Count());
    }

    [Fact]
    public void VariantSeparatesClientsThatAreToldDifferentTypes()
    {
      Assert.NotEqual(
        DlnaMaps.MimeVariant(Client(GENERIC)),
        DlnaMaps.MimeVariant(Client("SEC_HHP_[TV] Samsung")));
      Assert.Equal(
        DlnaMaps.MimeVariant(Client(GENERIC)),
        DlnaMaps.MimeVariant(null));
    }
  }
}
