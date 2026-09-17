using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Http = System.Net.Http;

namespace NMaier.SimpleDlna.Tests.Support
{
  internal sealed class BrowsedItem
  {
    public string Class;

    public string CoverUrl;

    public string Duration;

    public string Id;

    public string MimeType;

    public string Title;

    public string Url;
  }

  /// <summary>
  ///   A minimal UPnP client: enough SOAP to browse and query a server the way
  ///   a TV does, from a chosen local address and with a chosen User-Agent.
  /// </summary>
  internal static class DlnaClient
  {
    public const string GENERIC = "VLC/3.0.20 LibVLC/3.0.20";

    public const string SAMSUNG = "DLNADOC/1.50 SEC_HHP_[TV] Samsung Q70 Series (55)/1.0 UPnP/1.0";

    /// <summary>
    ///   The server is local and answers at once, so a short timeout turns a
    ///   hung response into a prompt failure.
    /// </summary>
    private static readonly Http.HttpClient http = new Http.HttpClient
    {
      Timeout = TimeSpan.FromSeconds(10)
    };

    private static CancellationToken Cancellation => Xunit.TestContext.Current.CancellationToken;

    public static Http.HttpResponseMessage Get(string url, string userAgent = GENERIC,
      bool firstByteOnly = false)
    {
      var request = new Http.HttpRequestMessage(Http.HttpMethod.Get, url);
      request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
      if (firstByteOnly) {
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
      }
      return http.Send(request, Cancellation);
    }

    public static byte[] GetBytes(string url, string userAgent = GENERIC)
    {
      using (var response = Get(url, userAgent)) {
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync(Cancellation).GetAwaiter().GetResult();
      }
    }

    private static Http.HttpResponseMessage Soap(string host, int port, string prefix,
      string service, string action, string body, string userAgent)
    {
      var envelope =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
        $"<s:Body><u:{action} xmlns:u=\"urn:schemas-upnp-org:service:{service}:1\">{body}</u:{action}></s:Body></s:Envelope>";
      var request = new Http.HttpRequestMessage(Http.HttpMethod.Post, $"http://{host}:{port}{prefix}control")
      {
        Content = new Http.StringContent(envelope, Encoding.UTF8)
      };
      request.Content.Headers.Remove("Content-Type");
      request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
      request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"urn:schemas-upnp-org:service:{service}:1#{action}\"");
      request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
      return http.Send(request, Cancellation);
    }

    /// <summary>
    ///   Browses the root of a mount. Returns the HTTP status and, on success,
    ///   the items.
    /// </summary>
    public static (HttpStatusCode Status, List<BrowsedItem> Items, string Didl) Browse(
      string host, int port, string prefix, string userAgent = GENERIC)
    {
      using (var response = Soap(host, port, prefix, "ContentDirectory", "Browse",
        "<ObjectID>0</ObjectID><BrowseFlag>BrowseDirectChildren</BrowseFlag><Filter>*</Filter>" +
        "<StartingIndex>0</StartingIndex><RequestedCount>100</RequestedCount><SortCriteria></SortCriteria>",
        userAgent)) {
        if (!response.IsSuccessStatusCode) {
          return (response.StatusCode, null, null);
        }
        var envelope = XDocument.Parse(response.Content.ReadAsStringAsync(Cancellation).GetAwaiter().GetResult());
        var didl = envelope.Descendants().First(e => e.Name.LocalName == "Result").Value;
        var items = XDocument.Parse(didl).Descendants()
          .Where(e => e.Name.LocalName == "item")
          .Select(item =>
          {
            var res = item.Elements().First(e => e.Name.LocalName == "res" && !e.Value.Contains("/cover/"));
            return new BrowsedItem
            {
              Id = item.Attribute("id")?.Value,
              Title = item.Elements().First(e => e.Name.LocalName == "title").Value,
              Class = item.Elements().First(e => e.Name.LocalName == "class").Value,
              Url = res.Value,
              MimeType = res.Attribute("protocolInfo")?.Value.Split(':')[2],
              Duration = res.Attribute("duration")?.Value,
              CoverUrl = item.Descendants().Select(e => e.Value).FirstOrDefault(v => v.Contains("/cover/"))
            };
          })
          .ToList();
        return (HttpStatusCode.OK, items, didl);
      }
    }

    public static string[] GetProtocolInfo(int port, string prefix, string userAgent)
    {
      using (var response = Soap("127.0.0.1", port, prefix, "ConnectionManager", "GetProtocolInfo", "", userAgent)) {
        response.EnsureSuccessStatusCode();
        var envelope = XDocument.Parse(response.Content.ReadAsStringAsync(Cancellation).GetAwaiter().GetResult());
        return envelope.Descendants().First(e => e.Name.LocalName == "Source").Value.Split(',');
      }
    }

    /// <summary>
    ///   Sends a GET over an already open connection, the way HttpClient reuses
    ///   one, and reads the whole response. Returns its headers, with names
    ///   in upper case.
    /// </summary>
    public static Dictionary<string, string> RawGet(Stream stream, string path,
      string extraHeaders = "")
    {
      var request = Encoding.ASCII.GetBytes(
        $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nUser-Agent: {GENERIC}\r\n{extraHeaders}\r\n");
      stream.Write(request, 0, request.Length);

      var head = new StringBuilder();
      while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) {
        var b = stream.ReadByte();
        if (b < 0) {
          throw new EndOfStreamException("connection closed before the response headers ended");
        }
        head.Append((char)b);
      }
      var headers = head.ToString()
        .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Skip(1)
        .Select(l => l.Split(':', 2))
        .ToDictionary(h => h[0].Trim().ToUpperInvariant(), h => h[1].Trim());
      string length;
      if (headers.TryGetValue("CONTENT-LENGTH", out length)) {
        stream.ReadExactly(new byte[long.Parse(length)]);
      }
      return headers;
    }

    /// <summary>
    ///   The host:port parts of every link in a browse result (res and cover
    ///   elements). Attributes are ignored: namespace declarations are URIs too.
    /// </summary>
    public static string[] LinkHosts(string didl)
    {
      return XDocument.Parse(didl).Descendants()
        .Where(e => !e.HasElements)
        .Select(e => Regex.Match(e.Value.Trim(), @"^https?://([^/]+)/"))
        .Where(m => m.Success)
        .Select(m => m.Groups[1].Value)
        .Distinct()
        .ToArray();
    }
  }
}
