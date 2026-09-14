using NMaier.SimpleDlna.Utilities;
using Xunit;

namespace NMaier.SimpleDlna.Tests
{
  public sealed class MacAddressTests
  {
    /// <summary>
    ///   Regression: octets were formatted with {:X}, dropping leading zeros,
    ///   so addresses with any octet below 0x10 never matched a restriction.
    /// </summary>
    [Fact]
    public void ResolvedAddressesUseTwoDigitsPerOctet()
    {
      var raw = new byte[] {0x01, 0xAF, 0xBC, 0x00, 0x0A, 0xFF};

      var mac = AddressToMacResolver.FormatMac(raw);

      Assert.Equal("01:AF:BC:00:0A:FF", mac);
      Assert.True(IP.IsAcceptedMAC(mac));
    }

    [Theory]
    [InlineData("01:af:bc:00:0a:ff", "01:AF:BC:00:0A:FF")]
    [InlineData("01-AF-BC-00-0A-FF", "01:AF:BC:00:0A:FF")]
    public void ConfiguredAddressesAreNormalised(string input, string expected)
    {
      string mac;
      Assert.True(ConfigurationValues.TryNormalizeMac(input, out mac));
      Assert.Equal(expected, mac);
    }

    [Theory]
    [InlineData("00:00:00:00")]
    [InlineData("1:AF:BC:0:A:FF")]
    [InlineData("xx01:AF:BC:00:0A:FFyy")]
    public void MalformedAddressesAreRejected(string input)
    {
      string mac;
      Assert.False(ConfigurationValues.TryNormalizeMac(input, out mac));
    }
  }
}
