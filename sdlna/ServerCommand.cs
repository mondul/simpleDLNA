using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace NMaier.SimpleDlna
{
  /// <summary>
  ///   A malformed --server command line. The message is shown as-is,
  ///   followed by a pointer to the usage text.
  /// </summary>
  internal sealed class UsageException : Exception
  {
    public UsageException(string message)
      : base(message)
    {
    }
  }

  /// <summary>
  ///   "sdlna --server": manages the servers in the configuration file.
  /// </summary>
  /// <remarks>
  ///   The grammar (set/unset and add/remove verbs, lists ended by "--",
  ///   switching restriction kinds mid-list) does not fit GetOptNet, so it is
  ///   parsed by hand. A "config" invocation may carry several options; they
  ///   are all applied to the in-memory configuration first and the file is
  ///   only written once every one of them has succeeded.
  /// </remarks>
  internal static class ServerCommand
  {
    private const string EDIT = "--edit";

    private const string EDITOR = "--editor";

    private const string FOLDERS = "--folders";

    private const string IP = "--ip";

    private const string MAC = "--mac";

    private const string MEDIA_TYPES = "--media-types";

    private const string RESTRICTIONS = "--restrictions";

    private const string SEPARATOR = "--";

    private const string SORT_ORDER = "--sort-order";

    private const string USER_AGENT = "--user-agent";

    private const string VIEWS = "--views";

    private const int LABEL_WIDTH = 14;

    private static readonly string[] sectionFlags =
    {
      MEDIA_TYPES, SORT_ORDER, FOLDERS, VIEWS, RESTRICTIONS, EDIT, EDITOR
    };

    private static readonly string[] restrictionFlags = {MAC, IP, USER_AGENT};

    public static int Run(IList<string> args)
    {
      try {
        if (args.Count == 0) {
          PrintUsage();
          return 0;
        }
        var rest = args.Skip(1).ToList();
        switch (args[0]) {
        case "help":
        case "--help":
        case "-h":
        case "-?":
          PrintUsage();
          return 0;
        case "add":
          return Add(rest);
        case "remove":
          return Remove(rest);
        case "config":
          return Config(rest);
        default:
          throw new UsageException(
            $"Unknown --server command \"{args[0]}\". Use add, remove or config.");
        }
      }
      catch (UsageException ex) {
        Console.Error.WriteLine("Error: {0}", ex.Message);
        Console.Error.WriteLine("Run 'sdlna --server help' for usage.");
        return 2;
      }
      catch (ConfigurationException ex) {
        Console.Error.WriteLine("Error: {0}", ex.Message);
        return 1;
      }
    }

    private static int Add(List<string> args)
    {
      if (args.Count < 2) {
        throw new UsageException(
          "add needs a server name and a folder: sdlna --server add <name> <folder>");
      }
      var name = ValidateName(args[0]);
      var config = ConfigurationStore.Load() ?? ConfigurationStore.CreateDefault();
      var existing = FindServer(config, name);
      if (existing != null) {
        throw new ConfigurationException(
          $"A server named \"{existing.Name}\" already exists. Change it with: sdlna --server config \"{existing.Name}\" ...");
      }

      var server = ConfigurationStore.CreateServer(name);
      var notes = new List<string>();
      var folders = args.Skip(1).ToList();
      var option = folders.FirstOrDefault(f => f.StartsWith("--", StringComparison.Ordinal));
      if (option != null) {
        throw new UsageException(
          $"add only takes a name and folders, not \"{option}\". Adjust the server afterwards with: sdlna --server config \"{name}\" {option} ...");
      }
      AddFolders(server, folders, notes);
      config.Servers.Add(server);
      ConfigurationStore.Save(config);

      notes.ForEach(Console.WriteLine);
      Console.WriteLine($"Added server \"{server.Name}\" to {ConfigurationStore.FilePath}");
      Console.WriteLine();
      PrintServer(server);
      Console.WriteLine();
      Console.WriteLine("Start all configured servers by running: sdlna");
      return 0;
    }

    private static int Remove(List<string> args)
    {
      if (args.Count != 1) {
        throw new UsageException(
          "remove needs exactly one server name: sdlna --server remove <name>");
      }
      var config = RequireConfiguration();
      var server = RequireServer(config, args[0]);
      config.Servers.Remove(server);
      ConfigurationStore.Save(config);

      Console.WriteLine($"Removed server \"{server.Name}\".");
      if (config.Servers.Count == 0) {
        Console.WriteLine(
          "No servers are configured now. Add one with: sdlna --server add <name> <folder>");
      }
      return 0;
    }

    private static int Config(List<string> args)
    {
      string name = null;
      if (args.Count > 0 && !args[0].StartsWith("-", StringComparison.Ordinal)) {
        name = args[0];
        args = args.Skip(1).ToList();
      }
      var sections = SplitSections(args);

      if (sections.Count == 0) {
        if (name == null) {
          PrintAll();
        }
        else {
          PrintServer(RequireServer(RequireConfiguration(), name));
        }
        return 0;
      }

      var fileOnly = sections.All(s => s.Flag == EDIT || s.Flag == EDITOR);
      if (name == null && !fileOnly) {
        var flag = sections.First(s => s.Flag != EDIT && s.Flag != EDITOR).Flag;
        throw new UsageException(
          $"{flag} changes one server; name it first: sdlna --server config <name> {flag} ...");
      }
      var edit = sections.Any(s => s.Flag == EDIT);

      SdlnaConfiguration config;
      try {
        config = ConfigurationStore.Load();
      }
      catch (ConfigurationException ex) {
        // A broken file must stay fixable, so --edit works without loading
        // it. Anything that needs the parsed settings cannot.
        if (edit && fileOnly) {
          return EditInvalidFile(ex, sections);
        }
        throw;
      }

      ServerConfiguration server = null;
      if (name != null) {
        server = RequireServer(
          config ?? throw NoConfiguration(), name);
      }
      config = config ?? ConfigurationStore.CreateDefault();

      var output = new List<string>();
      var changed = false;
      foreach (var section in sections) {
        switch (section.Flag) {
        case MEDIA_TYPES:
          changed |= ApplyMediaTypes(server, section.Args, output);
          break;
        case SORT_ORDER:
          changed |= ApplySortOrder(server, section.Args, output);
          break;
        case FOLDERS:
          changed |= ApplyFolders(server, section.Args, output);
          break;
        case VIEWS:
          changed |= ApplyViews(server, section.Args, output);
          break;
        case RESTRICTIONS:
          changed |= ApplyRestrictions(server, section.Args, output);
          break;
        case EDITOR:
          changed |= ApplyEditor(config, section.Args, output);
          break;
        case EDIT:
          if (section.Args.Count != 0) {
            throw new UsageException(
              $"--edit takes no values, but was given \"{section.Args[0]}\".");
          }
          break;
        }
      }

      var created = false;
      if (changed || (edit && !ConfigurationStore.Exists)) {
        created = !ConfigurationStore.Exists;
        ConfigurationStore.Save(config);
      }
      output.ForEach(Console.WriteLine);
      if (changed) {
        Console.WriteLine();
        Console.WriteLine($"Saved to {ConfigurationStore.FilePath}");
      }
      else if (created) {
        Console.WriteLine($"Created {ConfigurationStore.FilePath}");
      }
      return edit ? OpenEditor(config.Editor) : 0;
    }

    private sealed class Section
    {
      public readonly List<string> Args = new List<string>();

      public readonly string Flag;

      public Section(string flag)
      {
        Flag = flag;
      }
    }

    private static List<Section> SplitSections(IEnumerable<string> tokens)
    {
      var rv = new List<Section>();
      Section current = null;
      foreach (var token in tokens) {
        if (sectionFlags.Contains(token)) {
          current = new Section(token);
          rv.Add(current);
          continue;
        }
        if (current == null) {
          throw new UsageException(
            $"Unexpected \"{token}\". Expected an option such as {VIEWS} or {FOLDERS}.");
        }
        current.Args.Add(token);
      }
      return rv;
    }

    private static string Verb(string token, string section, params string[] verbs)
    {
      var verb = token.ToLowerInvariant();
      if (!verbs.Contains(verb)) {
        throw new UsageException(
          $"{section} expects {string.Join(" or ", verbs)} first, not \"{token}\".");
      }
      return verb;
    }

    /// <summary>
    ///   The values following a verb. A trailing "--" is accepted and
    ///   dropped; anything else starting with "--" is an error, since only
    ///   --restrictions uses separators and flags inside its list.
    /// </summary>
    private static List<string> Values(List<string> args, string section,
      string expected)
    {
      var values = WithoutTrailingSeparator(args.Skip(1));
      foreach (var v in values) {
        if (v == SEPARATOR) {
          throw new UsageException(
            $"-- only separates groups inside {RESTRICTIONS}, and cannot appear in the middle of {section}.");
        }
        if (v.StartsWith("--", StringComparison.Ordinal)) {
          throw new UsageException($"Unknown option \"{v}\" in {section}.");
        }
      }
      if (values.Count == 0) {
        throw new UsageException($"{section} {args[0]} needs {expected}.");
      }
      return values;
    }

    private static List<string> WithoutTrailingSeparator(IEnumerable<string> args)
    {
      var rv = args.ToList();
      if (rv.Count > 0 && rv[rv.Count - 1] == SEPARATOR) {
        rv.RemoveAt(rv.Count - 1);
      }
      return rv;
    }

    private static bool ApplyMediaTypes(ServerConfiguration server,
      List<string> args, List<string> output)
    {
      if (args.Count != 0) {
        var verb = Verb(args[0], MEDIA_TYPES, "set", "unset");
        var names = new List<string>();
        foreach (var value in Values(args, MEDIA_TYPES, "at least one of video, audio or images")) {
          string name;
          if (!MediaTypeNames.TryNormalize(value, out name)) {
            throw new UsageException(
              $"Unknown media type \"{value}\". Use video, audio or images.");
          }
          names.Add(name);
        }
        var result = verb == "set"
          ? server.MediaTypes.Union(names).ToList()
          : server.MediaTypes.Except(names).ToList();
        if (result.Count == 0) {
          throw new ConfigurationException(
            $"\"{server.Name}\" must keep at least one media type; unsetting {string.Join(", ", names)} would leave none.");
        }
        server.MediaTypes = MediaTypeNames.All.Where(result.Contains).ToList();
      }
      output.Add($"Media types of \"{server.Name}\": {string.Join(", ", server.MediaTypes)}");
      return args.Count != 0;
    }

    private static bool ApplySortOrder(ServerConfiguration server,
      List<string> args, List<string> output)
    {
      var values = WithoutTrailingSeparator(args);
      if (values.Count != 0) {
        if (values.Count > 2) {
          throw new UsageException(
            $"{SORT_ORDER} takes a field and an optional direction, e.g. {SORT_ORDER} date desc");
        }
        string order;
        if (!ConfigurationValues.TryNormalizeSortOrder(values[0], out order)) {
          throw new UsageException(
            $"Unknown sort order \"{values[0]}\". Use {string.Join(", ", ConfigurationValues.SortOrderNames)}.");
        }
        var direction = SortDirections.ASCENDING;
        if (values.Count == 2 && !SortDirections.TryNormalize(values[1], out direction)) {
          throw new UsageException(
            $"Unknown sort direction \"{values[1]}\". Use asc or desc.");
        }
        server.SortOrder = order;
        server.SortDirection = direction;
      }
      output.Add($"Sort order of \"{server.Name}\": {DescribeSortOrder(server)}");
      return values.Count != 0;
    }

    private static bool ApplyFolders(ServerConfiguration server,
      List<string> args, List<string> output)
    {
      if (args.Count != 0) {
        var verb = Verb(args[0], FOLDERS, "add", "remove");
        var values = Values(args, FOLDERS, "at least one folder");
        if (verb == "add") {
          AddFolders(server, values, output);
        }
        else {
          RemoveFolders(server, values);
        }
      }
      output.Add($"Folders of \"{server.Name}\":");
      output.AddRange(server.Folders.Select(f => "  " + f));
      return args.Count != 0;
    }

    private static void AddFolders(ServerConfiguration server,
      IEnumerable<string> values, List<string> output)
    {
      foreach (var value in values) {
        var folder = NormalizeFolder(value);
        if (!Directory.Exists(folder)) {
          throw new ConfigurationException($"Folder not found: {folder}");
        }
        var canonical = ConfigurationValues.CanonicalFolder(folder);
        var existing = server.Folders.FirstOrDefault(
          f => ConfigurationValues.PathComparer.Equals(CanonicalStoredFolder(f), canonical));
        if (existing != null) {
          output.Add(
            ConfigurationValues.PathComparer.Equals(existing, folder)
              ? $"Note: {folder} is already a folder of \"{server.Name}\"."
              : $"Note: {folder} is the same folder as {existing}, which \"{server.Name}\" already serves.");
          continue;
        }
        server.Folders.Add(folder);
      }
    }

    private static void RemoveFolders(ServerConfiguration server,
      IEnumerable<string> values)
    {
      foreach (var value in values) {
        var folder = NormalizeFolder(value);
        // Prefer the exact spelling, so that a server still listing two
        // spellings of one folder can be cleaned up one at a time.
        var match = server.Folders.FirstOrDefault(
                      f => ConfigurationValues.PathComparer.Equals(NormalizeStoredFolder(f), folder)) ??
                    server.Folders.FirstOrDefault(
                      f => ConfigurationValues.PathComparer.Equals(
                        CanonicalStoredFolder(f), CanonicalFolder(folder)));
        if (match == null) {
          throw new ConfigurationException(
            $"{folder} is not a folder of \"{server.Name}\".");
        }
        server.Folders.Remove(match);
      }
      if (server.Folders.Count == 0) {
        throw new ConfigurationException(
          $"\"{server.Name}\" needs at least one folder; add another before removing the last.");
      }
    }

    private static string NormalizeFolder(string value)
    {
      try {
        return ConfigurationValues.NormalizeFolder(value);
      }
      catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) {
        throw new ConfigurationException($"Not a valid folder path: \"{value}\"");
      }
    }

    private static string NormalizeStoredFolder(string value)
    {
      try {
        return ConfigurationValues.NormalizeFolder(value);
      }
      catch (Exception) {
        return value;
      }
    }

    private static string CanonicalFolder(string normalized)
    {
      try {
        return ConfigurationValues.CanonicalFolder(normalized);
      }
      catch (Exception) {
        return normalized;
      }
    }

    private static string CanonicalStoredFolder(string value)
    {
      return CanonicalFolder(NormalizeStoredFolder(value));
    }

    private static bool ApplyViews(ServerConfiguration server,
      List<string> args, List<string> output)
    {
      if (args.Count != 0) {
        var verb = Verb(args[0], VIEWS, "add", "remove");
        var values = Values(args, VIEWS, "at least one view, such as music or large:size=1000");
        foreach (var value in values) {
          if (verb == "add") {
            if (!ConfigurationValues.IsValidView(value)) {
              throw new ConfigurationException(
                $"Unknown view \"{value}\". See the list with: sdlna --list-views");
            }
            if (server.Views.Contains(value, StringComparer.OrdinalIgnoreCase)) {
              output.Add($"Note: \"{value}\" is already a view of \"{server.Name}\".");
              continue;
            }
            server.Views.Add(value);
            continue;
          }
          // Remove the exact entry, or else every entry of that view, so
          // that "large" removes "large:size=1000".
          var removed = server.Views.RemoveAll(
            v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
          if (removed == 0 && !value.Contains(':')) {
            removed = server.Views.RemoveAll(
              v => ViewName(v).Equals(value, StringComparison.OrdinalIgnoreCase));
          }
          if (removed == 0) {
            throw new ConfigurationException(
              $"\"{value}\" is not a view of \"{server.Name}\".");
          }
        }
      }
      if (server.Views.Count == 0) {
        output.Add($"Views of \"{server.Name}\": none");
      }
      else {
        output.Add($"Views of \"{server.Name}\", applied in this order:");
        output.AddRange(server.Views.Select(v => "  " + v));
      }
      return args.Count != 0;
    }

    private static string ViewName(string view)
    {
      var colon = view.IndexOf(':');
      return colon < 0 ? view : view.Substring(0, colon);
    }

    private sealed class RestrictionGroup
    {
      public readonly List<string> Values = new List<string>();

      public string Flag;

      public string Verb;
    }

    /// <summary>
    ///   Parses "add|remove --mac|--ip|--user-agent entry... [-- ...]". A
    ///   kind flag starts a new group under the current verb; "--" ends a
    ///   group and allows the verb to change before the next kind.
    /// </summary>
    private static List<RestrictionGroup> ParseRestrictions(List<string> args)
    {
      var groups = new List<RestrictionGroup>();
      string verb = null;
      RestrictionGroup current = null;
      foreach (var token in args) {
        if (token == SEPARATOR) {
          RequireEntries(current);
          current = null;
          continue;
        }
        var lower = token.ToLowerInvariant();
        if (current == null && (lower == "add" || lower == "remove")) {
          verb = lower;
          continue;
        }
        if (restrictionFlags.Contains(lower)) {
          if (verb == null) {
            throw new UsageException(
              $"{RESTRICTIONS} expects add or remove before {token}.");
          }
          RequireEntries(current);
          current = new RestrictionGroup {Verb = verb, Flag = lower};
          groups.Add(current);
          continue;
        }
        if (token.StartsWith("--", StringComparison.Ordinal)) {
          throw new UsageException(
            $"Unknown option \"{token}\" in {RESTRICTIONS}. Use {MAC}, {IP} or {USER_AGENT}.");
        }
        if (current == null) {
          throw new UsageException(
            verb == null
              ? $"{RESTRICTIONS} expects add or remove first, not \"{token}\"."
              : $"Say what kind of entry \"{token}\" is with {MAC}, {IP} or {USER_AGENT}.");
        }
        current.Values.Add(token);
      }
      RequireEntries(current);
      if (groups.Count == 0) {
        throw new UsageException(
          $"{RESTRICTIONS} add|remove needs {MAC}, {IP} or {USER_AGENT} followed by at least one entry.");
      }
      return groups;
    }

    private static void RequireEntries(RestrictionGroup group)
    {
      if (group != null && group.Values.Count == 0) {
        throw new UsageException($"{group.Flag} needs at least one entry.");
      }
    }

    private static bool ApplyRestrictions(ServerConfiguration server,
      List<string> args, List<string> output)
    {
      if (args.Count != 0) {
        var restrictions = server.Restrictions;
        var addedMac = false;
        var addedUserAgent = false;
        foreach (var group in ParseRestrictions(args)) {
          List<string> list;
          Func<string, string> normalize;
          StringComparer comparer;
          string kind;
          switch (group.Flag) {
          case MAC:
            kind = "MAC address";
            list = restrictions.Macs;
            comparer = StringComparer.OrdinalIgnoreCase;
            normalize = v =>
            {
              string mac;
              if (!ConfigurationValues.TryNormalizeMac(v, out mac)) {
                throw new ConfigurationException(
                  $"Invalid MAC address \"{v}\". Use six hex pairs, like 01:AF:BC:00:0A:FF.");
              }
              return mac;
            };
            break;
          case IP:
            kind = "IP address";
            list = restrictions.Ips;
            comparer = StringComparer.OrdinalIgnoreCase;
            normalize = v =>
            {
              string ip;
              if (!ConfigurationValues.TryNormalizeIp(v, out ip)) {
                throw new ConfigurationException($"Invalid IP address \"{v}\".");
              }
              return ip;
            };
            break;
          default:
            kind = "User-Agent";
            list = restrictions.UserAgents;
            // Matched exactly and case-sensitively by UserAgentAuthorizer.
            comparer = StringComparer.Ordinal;
            normalize = v =>
            {
              if (string.IsNullOrWhiteSpace(v)) {
                throw new ConfigurationException("A User-Agent entry cannot be empty.");
              }
              return v;
            };
            break;
          }

          foreach (var raw in group.Values) {
            var value = normalize(raw);
            if (group.Verb == "add") {
              if (list.Contains(value, comparer)) {
                output.Add($"Note: {kind} \"{value}\" is already allowed for \"{server.Name}\".");
                continue;
              }
              list.Add(value);
              addedMac |= group.Flag == MAC;
              addedUserAgent |= group.Flag == USER_AGENT;
              continue;
            }
            if (list.RemoveAll(e => comparer.Equals(e, value)) == 0) {
              throw new ConfigurationException(
                $"{kind} \"{value}\" is not among the restrictions of \"{server.Name}\".");
            }
          }
        }
        if (addedMac && !OperatingSystem.IsWindows()) {
          output.Add(
            "Warning: MAC addresses can only be looked up when sdlna runs on Windows. " +
            "On this system no client's MAC is known, so a MAC entry never matches.");
        }
        if (addedUserAgent) {
          output.Add(
            "Note: a User-Agent entry must equal the client's entire User-Agent header, including case.");
        }
      }

      var rows = RestrictionRows(server.Restrictions);
      if (rows.Count == 0) {
        output.Add($"Restrictions of \"{server.Name}\": none, so every client may connect");
      }
      else {
        output.Add($"Restrictions of \"{server.Name}\" (a client matching any entry may connect):");
        output.AddRange(rows.Select(r => "  " + r));
      }
      return args.Count != 0;
    }

    private static List<string> RestrictionRows(RestrictionsConfiguration restrictions)
    {
      const int width = 12;
      return restrictions.Macs.Select(m => "MAC".PadRight(width) + m)
        .Concat(restrictions.Ips.Select(i => "IP".PadRight(width) + i))
        .Concat(restrictions.UserAgents.Select(u => "User-Agent".PadRight(width) + u))
        .ToList();
    }

    private static bool ApplyEditor(SdlnaConfiguration config,
      List<string> args, List<string> output)
    {
      var values = WithoutTrailingSeparator(args);
      if (values.Count != 0) {
        var command = JoinCommand(values);
        if (string.IsNullOrWhiteSpace(command)) {
          throw new UsageException($"{EDITOR} needs a command, e.g. {EDITOR} vim");
        }
        config.Editor = command.Equals("default", StringComparison.OrdinalIgnoreCase)
          ? null
          : command;
      }
      output.Add($"Editor: {DescribeEditor(config)}");
      return values.Count != 0;
    }

    private static string JoinCommand(IList<string> values)
    {
      // A single argument is kept verbatim: it may be a path with spaces.
      return values.Count == 1
        ? values[0].Trim()
        : string.Join(" ", values.Select(v => v.Any(char.IsWhiteSpace) ? $"\"{v}\"" : v));
    }

    private static string DescribeEditor(SdlnaConfiguration config)
    {
      return string.IsNullOrWhiteSpace(config?.Editor)
        ? $"{ConfigurationStore.DefaultEditor} (default)"
        : config.Editor;
    }

    private static int EditInvalidFile(ConfigurationException error,
      List<Section> sections)
    {
      Console.Error.WriteLine("Warning: {0}", error.Message);
      Console.Error.WriteLine();
      string editor = null;
      var editorSection = sections.LastOrDefault(
        s => s.Flag == EDITOR && WithoutTrailingSeparator(s.Args).Count != 0);
      if (editorSection != null) {
        editor = JoinCommand(WithoutTrailingSeparator(editorSection.Args));
        Console.Error.WriteLine(
          "The {0} setting is used for this edit only; it can be saved once the file is valid.",
          EDITOR);
      }
      return OpenEditor(editor);
    }

    private static int OpenEditor(string editor)
    {
      var command = string.IsNullOrWhiteSpace(editor)
        ? ConfigurationStore.DefaultEditor
        : editor;
      var start = EditorStartInfo(command, ConfigurationStore.FilePath);
      try {
        using (var process = Process.Start(start)) {
          process?.WaitForExit();
        }
      }
      catch (Win32Exception ex) {
        throw new ConfigurationException(
          $"Could not start the editor \"{command}\": {ex.Message}. Choose another with: sdlna --server config {EDITOR} <command>");
      }

      // Report straight away if the edit left the file unusable.
      try {
        var config = ConfigurationStore.Load();
        var count = config?.Servers.Count ?? 0;
        Console.WriteLine(
          $"{ConfigurationStore.FilePath} is valid ({count} server{(count == 1 ? "" : "s")}).");
        return 0;
      }
      catch (ConfigurationException ex) {
        Console.Error.WriteLine("Error: {0}", ex.Message);
        return 1;
      }
    }

    private static ProcessStartInfo EditorStartInfo(string command, string file)
    {
      string program;
      List<string> arguments;
      // An existing path is taken whole, so an unquoted path containing
      // spaces still works.
      if (File.Exists(command)) {
        program = command;
        arguments = new List<string>();
      }
      else {
        var parts = SplitCommandLine(command);
        if (parts.Count == 0) {
          throw new ConfigurationException("The editor command is empty.");
        }
        program = parts[0];
        arguments = parts.Skip(1).ToList();
      }
      // No shell: the editor inherits this console, which terminal editors
      // such as nano need.
      var start = new ProcessStartInfo(program) {UseShellExecute = false};
      foreach (var argument in arguments) {
        start.ArgumentList.Add(argument);
      }
      start.ArgumentList.Add(file);
      return start;
    }

    private static List<string> SplitCommandLine(string command)
    {
      var rv = new List<string>();
      var current = new StringBuilder();
      var quote = '\0';
      var inToken = false;
      foreach (var c in command) {
        if (quote != '\0') {
          if (c == quote) {
            quote = '\0';
          }
          else {
            current.Append(c);
          }
          continue;
        }
        if (c == '"' || c == '\'') {
          quote = c;
          inToken = true;
          continue;
        }
        if (char.IsWhiteSpace(c)) {
          if (inToken) {
            rv.Add(current.ToString());
            current.Clear();
            inToken = false;
          }
          continue;
        }
        current.Append(c);
        inToken = true;
      }
      if (inToken) {
        rv.Add(current.ToString());
      }
      return rv;
    }

    private static string ValidateName(string name)
    {
      var rv = name?.Trim();
      if (string.IsNullOrEmpty(rv)) {
        throw new UsageException("A server name cannot be empty.");
      }
      if (rv.StartsWith("-", StringComparison.Ordinal)) {
        throw new UsageException(
          $"A server name cannot start with \"-\" (got \"{rv}\").");
      }
      return rv;
    }

    private static ConfigurationException NoConfiguration()
    {
      return new ConfigurationException(
        "No servers are configured yet. Add one with: sdlna --server add <name> <folder>");
    }

    private static SdlnaConfiguration RequireConfiguration()
    {
      return ConfigurationStore.Load() ?? throw NoConfiguration();
    }

    private static ServerConfiguration FindServer(SdlnaConfiguration config,
      string name)
    {
      return config.Servers.FirstOrDefault(
        s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static ServerConfiguration RequireServer(SdlnaConfiguration config,
      string name)
    {
      var server = FindServer(config, name);
      if (server != null) {
        return server;
      }
      var names = config.Servers.Select(s => $"\"{s.Name}\"").ToList();
      throw new ConfigurationException(
        names.Count == 0
          ? $"There is no server named \"{name}\"; none are configured. Add one with: sdlna --server add <name> <folder>"
          : $"There is no server named \"{name}\". Configured servers: {string.Join(", ", names)}");
    }

    private static string DescribeSortOrder(ServerConfiguration server)
    {
      return $"{server.SortOrder}, {(server.Descending ? "descending" : "ascending")}";
    }

    private static void AddRow(ICollection<string> lines, string label,
      IList<string> values)
    {
      if (values.Count == 0) {
        values = new[] {"none"};
      }
      lines.Add("  " + label.PadRight(LABEL_WIDTH) + values[0]);
      foreach (var v in values.Skip(1)) {
        lines.Add("  " + new string(' ', LABEL_WIDTH) + v);
      }
    }

    private static void PrintServer(ServerConfiguration server)
    {
      var lines = new List<string> {server.Name};
      AddRow(lines, "Folders", server.Folders);
      AddRow(lines, "Media types", new[] {string.Join(", ", server.MediaTypes)});
      AddRow(lines, "Sort order", new[] {DescribeSortOrder(server)});
      AddRow(lines, "Views", server.Views.Count == 0
               ? new string[0]
               : new[] {string.Join(", ", server.Views)});
      var restrictions = RestrictionRows(server.Restrictions);
      AddRow(lines, "Restrictions", restrictions.Count == 0
               ? new[] {"none, every client may connect"}
               : restrictions);
      lines.ForEach(Console.WriteLine);
    }

    private static void PrintAll()
    {
      var config = ConfigurationStore.Load();
      Console.WriteLine($"Configuration  {ConfigurationStore.FilePath}");
      if (config == null) {
        Console.WriteLine("               (not created yet)");
        Console.WriteLine();
        Console.WriteLine("No servers are configured. Add one with: sdlna --server add <name> <folder>");
        return;
      }
      var cache = ConfigurationStore.ResolveCache(config);
      Console.WriteLine($"Port           {(config.Port == 0 ? "0 (any free port)" : config.Port.ToString())}");
      Console.WriteLine($"Cache          {(cache == null ? "none" : cache.FullName)}");
      Console.WriteLine($"Editor         {DescribeEditor(config)}");
      Console.WriteLine();
      if (config.Servers.Count == 0) {
        Console.WriteLine("No servers are configured. Add one with: sdlna --server add <name> <folder>");
        return;
      }
      for (var i = 0; i < config.Servers.Count; ++i) {
        if (i != 0) {
          Console.WriteLine();
        }
        PrintServer(config.Servers[i]);
      }
    }

    private static void PrintUsage()
    {
      Console.WriteLine(
$@"Manage the media servers saved in the configuration file. Running 'sdlna'
with no folders starts all of them.

Configuration file: {ConfigurationStore.FilePath}

Usage:
  sdlna --server add <name> <folder>...   Add a server
  sdlna --server remove <name>            Remove a server
  sdlna --server config                   Show all settings and servers
  sdlna --server config <name>            Show one server's settings
  sdlna --server config <name> <option>...
                                          Change one server's settings
  sdlna --server config --edit            Open the file in a text editor
  sdlna --server config --editor [<command>|default]
                                          Show or set that editor
                                          (default: {ConfigurationStore.DefaultEditor})

A new server serves video, audio and images, sorted by title ascending, with
no views and no restrictions.

Server options. Each one given without a value shows the current setting.

  {MEDIA_TYPES} set|unset <type>...
      Types to serve: video, audio, images. At least one must stay set.

  {SORT_ORDER} date|size|title [asc|desc]
      How items are ordered. The direction defaults to asc.

  {FOLDERS} add|remove <folder>...
      Folders to serve. A server needs at least one.

  {VIEWS} add|remove <view>...
      Views reshape or filter what clients see, and are applied in the order
      they were added. They take the same values as -v, e.g. music, new,
      large:size=1000; see 'sdlna --list-views'. 'remove large' removes every
      large view, whatever its options.

  {RESTRICTIONS} add|remove {MAC}|{IP}|{USER_AGENT} <entry>... [-- ...]
      Allow only clients matching an entry; with no entries, every client
      may connect. Separate groups with --, which also lets you switch
      between add and remove. User-Agent entries must match the client's
      whole header exactly. MAC entries only match when sdlna runs on
      Windows. Clients on this machine are always allowed.

Several options can be combined in one call. Nothing is saved unless all of
them succeed.

The port and cache location are set in the file itself; use --edit.

Examples:
  sdlna --server add Movies ~/Movies
  sdlna --server config Movies {MEDIA_TYPES} unset audio images
  sdlna --server config Movies {SORT_ORDER} date desc {VIEWS} add new series
  sdlna --server config Movies {RESTRICTIONS} add {IP} 192.168.1.20 -- {MAC} 01:AF:BC:00:0A:FF
  sdlna --server config --editor ""code --wait""");
    }
  }
}
