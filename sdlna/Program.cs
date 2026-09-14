using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using log4net;
using NMaier.GetOptNet;
using NMaier.SimpleDlna.FileMediaServer;
using NMaier.SimpleDlna.Properties;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Comparers;
using NMaier.SimpleDlna.Server.Views;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna
{
  public static class Program
  {
    private static readonly ManualResetEvent blockEvent =
      new ManualResetEvent(false);

    private static uint cancelHitCount;

    private static void CancelKeyPressed(object sender,
      ConsoleCancelEventArgs e)
    {
      if (cancelHitCount++ == 3) {
        LogManager.GetLogger(typeof (Program)).Fatal(
          "Emergency exit commencing");
        return;
      }
      e.Cancel = true;
      blockEvent.Set();
      LogManager.GetLogger(typeof (Program)).Info("Shutdown requested");
      Console.Title = "SimpleDLNA - shutting down ...";
    }

    private static void ListOrders()
    {
      var items = from v in ComparerRepository.ListItems()
                  orderby v.Key
                  select v.Value;
      Console.WriteLine("Available orders:");
      Console.WriteLine("----------------");
      Console.WriteLine();
      foreach (var i in items) {
        Console.WriteLine("  - " + i);
        Console.WriteLine();
      }
    }

    private static void ListViews()
    {
      var items = from v in ViewRepository.ListItems()
                  orderby v.Key
                  select v.Value;
      Console.WriteLine("Available views:");
      Console.WriteLine("----------------");
      Console.WriteLine();
      foreach (var i in items) {
        Console.WriteLine("  - " + i);
        Console.WriteLine();
      }
    }

    private static void Main(string[] args)
    {
      Console.WriteLine();
      var options = new Options();
      try {
        Console.TreatControlCAsInput = false;
        Console.CancelKeyPress += CancelKeyPressed;

        options.Parse(args);
        if (options.ShowHelp) {
          options.PrintUsage();
          return;
        }
        if (options.ShowVersion) {
          ShowVersion();
          return;
        }
        if (options.ShowLicense) {
          ShowLicense();
          return;
        }
        if (options.ListViews) {
          ListViews();
          return;
        }
        if (options.ListOrders) {
          ListOrders();
          return;
        }
        if (options.Directories.Length == 0) {
          RunConfiguredServers(options);
          return;
        }

        options.SetupLogging();

        using (new ProgramIcon()) {
          var server = new HttpServer(options.Port);
          try {
            using (var authorizer = new HttpAuthorizer(server)) {
              if (options.Ips.Length != 0) {
                authorizer.AddMethod(new IPAddressAuthorizer(options.Ips));
              }
              if (options.Macs.Length != 0) {
                authorizer.AddMethod(new MacAuthorizer(options.Macs));
              }
              if (options.UserAgents.Length != 0) {
                authorizer.AddMethod(
                  new UserAgentAuthorizer(options.UserAgents));
              }

              Console.Title = "SimpleDLNA - starting ...";

              var types = options.Types[0];
              foreach (var t in options.Types) {
                types = types | t;
                server.InfoFormat("Enabled type {0}", t);
              }

              var friendlyName = "sdlna";

              if (options.Seperate) {
                foreach (var d in options.Directories) {
                  server.InfoFormat("Mounting FileServer for {0}", d.FullName);
                  var fs = SetupFileServer(
                    options, types, new[] {d});
                  friendlyName = fs.FriendlyName;
                  server.RegisterMediaServer(fs);
                  server.NoticeFormat("{0} mounted", d.FullName);
                }
              }
              else {
                server.InfoFormat(
                  "Mounting FileServer for {0} ({1})",
                  options.Directories[0], options.Directories.Length);
                var fs = SetupFileServer(options, types, options.Directories);
                friendlyName = fs.FriendlyName;
                server.RegisterMediaServer(fs);
                server.NoticeFormat(
                  "{0} ({1}) mounted",
                  options.Directories[0], options.Directories.Length);
              }

              Console.Title = $"{friendlyName} - running ...";

              Run(server);
            }
          }
          finally {
            server.Dispose();
          }
        }
      }
      catch (GetOptException ex) {
        Console.Error.WriteLine("Error: {0}\n\n", ex.Message);
        options.PrintUsage();
      }
      catch (ConfigurationException ex) {
        Console.Error.WriteLine("Error: {0}", ex.Message);
        Environment.ExitCode = 1;
      }
#if !DEBUG
      catch (Exception ex) {
        LogManager.GetLogger(typeof (Program)).Fatal("Failed to run", ex);
      }
#endif
    }

    private static void PrintNoServersConfigured(bool fileExists)
    {
      Console.WriteLine(
        fileExists
          ? $"No media servers are configured in {ConfigurationStore.FilePath}."
          : "No media servers are configured yet.");
      Console.WriteLine();
      Console.WriteLine("Set one up. It is saved, and started every time you run sdlna:");
      Console.WriteLine("  sdlna --server add <name> <folder>");
      Console.WriteLine();
      Console.WriteLine("Or serve folders just this once, without saving anything:");
      Console.WriteLine("  sdlna <folder> [<folder>...]");
      Console.WriteLine();
      Console.WriteLine("Run 'sdlna --server help' to manage servers, or 'sdlna --help' for all options.");
    }

    private static void Run(HttpServer server)
    {
      server.Info("CTRL-C to terminate");
      blockEvent.WaitOne();

      server.Info("Going down!");
      server.Info("Closed!");
    }

    private static FileServer SetupFileServer(Options options,
      DlnaMediaTypes types,
      DirectoryInfo[] d)
    {
      var ids = new Identifiers(
        ComparerRepository.Lookup(options.Order), options.DescendingOrder);
      foreach (var v in options.Views) {
        try {
          ids.AddView(v);
        }
        catch (RepositoryLookupException) {
          throw new GetOptException("Invalid view " + v);
        }
      }
      var fs = new FileServer(types, ids, d);
      try {
        if (!string.IsNullOrEmpty(options.FriendlyName)) {
          fs.FriendlyName = options.FriendlyName;
        }
        if (options.CacheFile != null) {
          fs.SetCacheFile(options.CacheFile);
        }
        fs.Load();
        if (!options.Rescanning) {
          fs.Rescanning = false;
        }
      }
      catch (Exception) {
        fs.Dispose();
        throw;
      }
      return fs;
    }

    /// <summary>
    ///   Starts every server in the configuration file on one HTTP server.
    ///   Only the process-wide options (port, cache, logging, rescanning)
    ///   come from the command line here; everything else is per server.
    /// </summary>
    private static void RunConfiguredServers(Options options)
    {
      var given = options.GivenServerOptions().ToList();
      if (given.Count != 0) {
        Console.Error.WriteLine(
          "Error: these options only apply when folders are given on the command line: {0}",
          string.Join(", ", given));
        Console.Error.WriteLine(
          "Configured servers take these settings from {0}; change them with 'sdlna --server config <name> ...'.",
          ConfigurationStore.FilePath);
        Environment.ExitCode = 2;
        return;
      }

      var config = ConfigurationStore.Load();
      if (config == null || config.Servers.Count == 0) {
        PrintNoServersConfigured(config != null);
        Environment.ExitCode = 1;
        return;
      }

      options.SetupLogging();

      var port = options.PortSpecified ? options.Port : config.Port;
      var cacheFile = options.CacheFile ?? ConfigurationStore.ResolveCache(config);
      if (cacheFile?.Directory != null && !cacheFile.Directory.Exists) {
        cacheFile.Directory.Create();
      }

      using (new ProgramIcon()) {
        var httpServer = new HttpServer(port);
        try {
          Console.Title = "SimpleDLNA - starting ...";
          var mounted = 0;
          foreach (var server in config.Servers) {
            try {
              httpServer.InfoFormat("Mounting server {0}", server.Name);
              var fs = SetupConfiguredServer(server, cacheFile, options.Rescanning, httpServer);
              httpServer.RegisterMediaServer(fs);
              ++mounted;
              httpServer.NoticeFormat("{0} mounted", server.Name);
            }
            catch (Exception ex) {
              httpServer.ErrorFormat("Failed to start server {0}: {1}", server.Name, ex.Message);
            }
          }
          if (mounted == 0) {
            throw new ConfigurationException("None of the configured servers could be started.");
          }

          Console.Title = mounted == 1
            ? $"{config.Servers[0].Name} - running ..."
            : $"SimpleDLNA - {mounted} servers running ...";

          Run(httpServer);
        }
        finally {
          httpServer.Dispose();
        }
      }
    }

    private static FileServer SetupConfiguredServer(ServerConfiguration config,
      FileInfo cacheFile, bool rescanning, HttpServer httpServer)
    {
      // A folder may be on a drive that is not mounted right now; serve the
      // rest rather than refusing the whole server.
      var folders = new List<DirectoryInfo>();
      foreach (var f in config.Folders) {
        var folder = new DirectoryInfo(f);
        if (folder.Exists) {
          folders.Add(folder);
        }
        else {
          httpServer.WarnFormat("{0}: skipping missing folder {1}", config.Name, folder.FullName);
        }
      }
      if (folders.Count == 0) {
        throw new InvalidOperationException("none of its folders exist");
      }

      var ids = new Identifiers(
        ComparerRepository.Lookup(config.SortOrder), config.Descending);
      foreach (var v in config.Views) {
        ids.AddView(v);
      }

      var fs = new FileServer(
        MediaTypeNames.ToDlnaMediaTypes(config.MediaTypes), ids, folders.ToArray());
      try {
        fs.FriendlyName = config.Name;
        if (cacheFile != null) {
          fs.SetCacheFile(cacheFile);
        }
        fs.Load();
        if (!rescanning) {
          fs.Rescanning = false;
        }

        // Restrictions are per server, unlike the ad-hoc -i/-m/-u, which
        // guard the whole HTTP server. An empty set admits everyone.
        var restrictions = config.Restrictions;
        if (!restrictions.IsEmpty) {
          var authorizer = new HttpAuthorizer();
          if (restrictions.Ips.Count != 0) {
            authorizer.AddMethod(new IPAddressAuthorizer(restrictions.Ips));
          }
          if (restrictions.Macs.Count != 0) {
            authorizer.AddMethod(new MacAuthorizer(restrictions.Macs));
          }
          if (restrictions.UserAgents.Count != 0) {
            authorizer.AddMethod(new UserAgentAuthorizer(restrictions.UserAgents));
          }
          fs.Authorizer = authorizer;
        }
      }
      catch (Exception) {
        fs.Dispose();
        throw;
      }
      return fs;
    }

    private static void ShowLicense()
    {
      Console.WriteLine(ProductInformation.Copyright);
      Console.WriteLine();
      Console.Write(Encoding.UTF8.GetString(Resources.LICENSE));
    }

    private static void ShowVersion()
    {
      Console.WriteLine("Version: {0}", ProductInformation.ProductVersion);
      Console.WriteLine("Http:    {0}", HttpServer.Signature);
    }
  }
}
