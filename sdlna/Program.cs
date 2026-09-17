using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    /// <summary>
    ///   Kept referenced: the handler is unregistered when this is collected.
    /// </summary>
    private static PosixSignalRegistration terminateRegistration;

    private static void CancelKeyPressed(object sender,
      ConsoleCancelEventArgs e)
    {
      if (cancelHitCount++ == 3) {
        LogManager.GetLogger(typeof (Program)).Fatal(
          "Emergency exit commencing");
        return;
      }
      e.Cancel = true;
      RequestShutdown("Shutdown requested");
    }

    /// <summary>
    ///   Services and containers are stopped with SIGTERM (systemctl stop,
    ///   docker stop), which otherwise ends the process without the shutdown
    ///   Ctrl+C gets, leaving the caches unclosed.
    /// </summary>
    private static void TerminateRequested(PosixSignalContext context)
    {
      context.Cancel = true;
      RequestShutdown("Termination requested");
    }

    private static void RequestShutdown(string reason)
    {
      blockEvent.Set();
      LogManager.GetLogger(typeof (Program)).Info(reason);
      SetTitle("SimpleDLNA - shutting down ...");
    }

    /// <summary>
    ///   Sets the console window's title, if there is a console window: a
    ///   service or scheduled task may run without one.
    /// </summary>
    private static void SetTitle(string title)
    {
      try {
        Console.Title = title;
      }
      catch (Exception ex) when (
        ex is IOException || ex is PlatformNotSupportedException ||
        ex is System.ComponentModel.Win32Exception) {
        // No console to title.
      }
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

    /// <summary>
    ///   What each view does and which options it takes. The one-line
    ///   descriptions in the view classes do not mention options at all, and
    ///   several are too vague to choose by. A view missing from this table
    ///   still gets listed, with its own description.
    /// </summary>
    private static readonly (string Name, bool Filter, string Text)[] viewHelp =
    {
      ("bytitle", false,
       "Sorts every file into A-Z folders by the first letter of its title.\n" +
       "Letters with over 100 files are split further by the words the\n" +
       "titles start with."),
      ("flatten", false,
       "Dissolves folders holding three files or fewer into their parent,\n" +
       "so sparse, deeply nested trees become shallower."),
      ("music", false,
       "Adds Artists, Performers, Albums and Genre folders built from the\n" +
       "files' tags. The original folders move under \"Folders\"."),
      ("plain", false,
       "Puts every file directly in the top folder."),
      ("series", false,
       "Gives each TV show with at least two episodes its own folder,\n" +
       "recognising names like \"Show S01E02\", \"Show 1x02\" or dated names.\n" +
       "Options: no-cascade"),
      ("sites", false,
       "Gives each site with at least two files its own folder, recognising\n" +
       "names like \"[site] title\" or \"site - title\".\n" +
       "Options: no-cascade"),
      ("dimension", true,
       "Only images and videos whose pixel size is known and in range.\n" +
       "Options: min (shorter side), max (longer side), minwidth, maxwidth,\n" +
       "minheight, maxheight"),
      ("filter", true,
       "Only files whose title or path contains one of the given words,\n" +
       "ignoring case. A word containing * or ? must match the whole title\n" +
       "or path instead.\n" +
       "Options: the words themselves"),
      ("large", true,
       "Only files of at least 300 MB.\n" +
       "Options: size, in MB"),
      ("new", true,
       "Only files modified in the last 7 days.\n" +
       "Options: date, the earliest modification date, e.g. 2026-01-31")
    };

    private static void ListViews()
    {
      const int width = 11;
      var available = ViewRepository.ListItems();

      void Print(string name, string text)
      {
        var lines = text.Split('\n');
        Console.WriteLine("  " + name.PadRight(width) + lines[0]);
        foreach (var line in lines.Skip(1)) {
          Console.WriteLine("  " + new string(' ', width) + line);
        }
      }

      void PrintGroup(string heading, bool filter)
      {
        Console.WriteLine(heading);
        Console.WriteLine();
        foreach (var v in viewHelp.Where(v => v.Filter == filter && available.ContainsKey(v.Name))) {
          Print(v.Name, v.Text);
        }
        Console.WriteLine();
      }

      Console.WriteLine("Views change how clients see your media, without touching the files.");
      Console.WriteLine("Apply them with -v, or with --views add for a configured server. Several");
      Console.WriteLine("views are applied in the order given, each working on the result of the");
      Console.WriteLine("one before.");
      Console.WriteLine();
      PrintGroup("Reorganizing views build new virtual folders:", false);
      PrintGroup("Filtering views hide items that do not match:", true);

      var undocumented = available
        .Where(a => viewHelp.All(v => !v.Name.Equals(a.Key, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(a => a.Key)
        .ToList();
      if (undocumented.Count != 0) {
        Console.WriteLine("Other views:");
        Console.WriteLine();
        foreach (var v in undocumented) {
          Print(v.Value.Name, v.Value.Description);
        }
        Console.WriteLine();
      }

      Console.WriteLine("Options follow the view's name after a colon, separated by commas:");
      Console.WriteLine();
      Console.WriteLine("  sdlna -v large:size=1000 ~/Videos");
      Console.WriteLine("  sdlna -v new:date=2026-01-31 -v series ~/TV");
      Console.WriteLine("  sdlna -v filter:holiday,beach -v dimension:min=1080 ~/Pictures");
      Console.WriteLine();
      Console.WriteLine("series and sites gather their folders into A-Z folders once there are more");
      Console.WriteLine("than 50 of them; no-cascade keeps them all at the top.");
    }

    private static void Main(string[] args)
    {
      Console.WriteLine();
      if (args.Length > 0 && args[0] == "--server") {
        Environment.ExitCode = ServerCommand.Run(args.Skip(1).ToList());
        return;
      }
      if (args.Contains("--server")) {
        Console.Error.WriteLine("Error: --server must be the first argument, e.g. sdlna --server help");
        Environment.ExitCode = 2;
        return;
      }

      var options = new Options();
      try {
        // On Windows this changes the console's input mode, and throws when
        // standard input is not a console: a background job, a scheduled task,
        // a service wrapper. Ctrl+C can't arrive through such an input anyway.
        if (!Console.IsInputRedirected) {
          Console.TreatControlCAsInput = false;
        }
        Console.CancelKeyPress += CancelKeyPressed;
        try {
          terminateRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM, TerminateRequested);
        }
        catch (PlatformNotSupportedException) {
          // Ctrl+C still works.
        }

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
          var config = ChooseConfiguredServers(options, Console.Error);
          if (config != null) {
            RunConfiguredServers(options, config);
            return;
          }
        }

        options.SetupLogging();

        using (new ProgramIcon()) {
          var server = new HttpServer(options.Port);
          var fileServers = new List<FileServer>();
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

              SetTitle("SimpleDLNA - starting ...");

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
                  fileServers.Add(fs);
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
                fileServers.Add(fs);
                friendlyName = fs.FriendlyName;
                server.RegisterMediaServer(fs);
                server.NoticeFormat(
                  "{0} ({1}) mounted",
                  options.Directories[0], options.Directories.Length);
              }

              SetTitle($"{friendlyName} - running ...");

              Run(server);
            }
          }
          finally {
            server.Dispose();
            DisposeAll(fileServers);
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
        // Logging may not be set up yet, and then the line above goes
        // nowhere: without this, sdlna would exit silently with status 0.
        Console.Error.WriteLine("Error: {0}", ex.Message);
        Environment.ExitCode = 1;
      }
#endif
    }

    /// <summary>
    ///   Decides what a run without folders does.
    /// </summary>
    /// <returns>
    ///   The configuration whose servers should start. When none are
    ///   configured, returns null after writing a warning and pointing
    ///   <paramref name="options" /> at the current directory, so the run
    ///   continues exactly as "sdlna ." would.
    /// </returns>
    /// <exception cref="ConfigurationException">
    ///   The configuration file is invalid. That is reported rather than
    ///   silently bypassed.
    /// </exception>
    internal static SdlnaConfiguration ChooseConfiguredServers(Options options,
      TextWriter warnings)
    {
      var config = ConfigurationStore.Load();
      if (config != null && config.Servers.Count != 0) {
        return config;
      }
      // Zero-config: nothing is saved, so serve the current directory with
      // every command-line option, and say how to save servers instead of
      // refusing to start.
      WarnServingCurrentDirectory(config != null, warnings);
      options.Directories = new[] {new DirectoryInfo(".")};
      return null;
    }

    private static void WarnServingCurrentDirectory(bool fileExists,
      TextWriter warnings)
    {
      warnings.WriteLine(
        fileExists
          ? $"Warning: No servers are configured in {ConfigurationStore.FilePath},"
          : "Warning: No servers are configured and no configuration file exists,");
      warnings.WriteLine("so serving the current directory:");
      warnings.WriteLine("  {0}", Path.GetFullPath("."));
      warnings.WriteLine();
      warnings.WriteLine(
        "Nothing has been saved. To set up servers that 'sdlna' starts every time, add one with:");
      warnings.WriteLine("  sdlna --server add <name> <folder>");
      warnings.WriteLine("See 'sdlna --server help' for more.");
      warnings.WriteLine();
    }

    /// <summary>
    ///   HttpServer only unregisters its media servers. Disposing them closes
    ///   their caches, which checkpoints the database and releases its lock.
    /// </summary>
    private static void DisposeAll(IEnumerable<FileServer> fileServers)
    {
      foreach (var fs in fileServers) {
        try {
          fs.Dispose();
        }
        catch (Exception ex) {
          LogManager.GetLogger(typeof (Program)).Warn(
            $"Failed to shut down {fs.FriendlyName}", ex);
        }
      }
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
    private static void RunConfiguredServers(Options options,
      SdlnaConfiguration config)
    {
      var given = options.GivenServerOptions().ToList();
      if (given.Count != 0) {
        Console.Error.WriteLine(
          "Error: {0} cannot be used with the servers configured in {1}, which each have their own settings.",
          string.Join(", ", given), ConfigurationStore.FilePath);
        Console.Error.WriteLine(
          "Change those with 'sdlna --server config <name> ...', or give folders to serve them with these options instead.");
        Environment.ExitCode = 2;
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
        var fileServers = new List<FileServer>();
        try {
          SetTitle("SimpleDLNA - starting ...");
          var mounted = 0;
          foreach (var server in config.Servers) {
            try {
              httpServer.InfoFormat("Mounting server {0}", server.Name);
              var fs = SetupConfiguredServer(server, cacheFile, options.Rescanning, httpServer);
              fileServers.Add(fs);
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

          SetTitle(mounted == 1
            ? $"{config.Servers[0].Name} - running ..."
            : $"SimpleDLNA - {mounted} servers running ...");

          Run(httpServer);
        }
        finally {
          httpServer.Dispose();
          DisposeAll(fileServers);
        }
      }
    }

    internal static FileServer SetupConfiguredServer(ServerConfiguration config,
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
