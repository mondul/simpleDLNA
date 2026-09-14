using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Server;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   A real HttpServer on a free port, shared by the HTTP server collection:
  ///   tests mount their own file servers on it and unmount them after. SSDP
  ///   is off, so test servers are never announced to TVs on the network.
  /// </summary>
  public sealed class DlnaTestServer : IDisposable
  {
    private readonly List<FileServer> mounted = new List<FileServer>();

    public DlnaTestServer()
    {
      Http = new HttpServer(0, false);
    }

    public HttpServer Http { get; }

    public int Port => Http.RealPort;

    public void Dispose()
    {
      UnmountAll();
      Http.Dispose();
    }

    /// <summary>
    ///   Registers a file server and returns its mount prefix ("/mm-N/").
    ///   Prefixes come from a process-wide counter, so they are discovered from
    ///   the server's index page and each mount's device description.
    /// </summary>
    /// <param name="fileServer">The file server to host.</param>
    /// <param name="loaded">
    ///   Whether Load() was already called. It must not be called twice: each
    ///   call subscribes the rescan handlers again.
    /// </param>
    public string Mount(FileServer fileServer, bool loaded = false)
    {
      var name = "test-" + Guid.NewGuid().ToString("N");
      fileServer.FriendlyName = name;
      if (!loaded) {
        fileServer.Rescanning = false;
        fileServer.Load();
      }
      Http.RegisterMediaServer(fileServer);
      mounted.Add(fileServer);

      var index = DlnaClient.GetBytes($"http://127.0.0.1:{Port}/");
      foreach (Match m in Regex.Matches(System.Text.Encoding.UTF8.GetString(index), "href=\"(/mm-\\d+/)\"")) {
        var prefix = m.Groups[1].Value;
        var description = System.Text.Encoding.UTF8.GetString(
          DlnaClient.GetBytes($"http://127.0.0.1:{Port}{prefix}description.xml"));
        if (description.Contains(name)) {
          return prefix;
        }
      }
      throw new InvalidOperationException($"mount for {name} not found");
    }

    /// <summary>
    ///   Unregisters and disposes every file server mounted so far.
    /// </summary>
    public void UnmountAll()
    {
      foreach (var fileServer in mounted) {
        Http.UnregisterMediaServer(fileServer);
        fileServer.Dispose();
      }
      mounted.Clear();
    }

    /// <summary>
    ///   An IPv4 address of this machine other than loopback, or null. The
    ///   server always admits loopback clients, so restrictions can only be
    ///   observed through such an address.
    /// </summary>
    public static string NonLoopbackAddress()
    {
      return NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a) &&
                    !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(a => a.ToString())
        .FirstOrDefault();
    }
  }
}
