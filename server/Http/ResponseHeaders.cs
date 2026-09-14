using System;

namespace NMaier.SimpleDlna.Server
{
  public sealed class ResponseHeaders : RawHeaders
  {
    public ResponseHeaders()
      : this(true)
    {
    }

    public ResponseHeaders(bool noCache)
    {
      this["Server"] = HttpServer.Signature;
      this["Date"] = DateTime.Now.ToString("R");
      // No Connection header: HttpClient.SendResponse writes it, since only
      // the connection knows whether it will be kept open.
      if (noCache) {
        this["Cache-Control"] = "no-cache";
      }
    }
  }
}
