using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Server.Comparers;
using NMaier.SimpleDlna.Server.Views;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna
{
  /// <summary>
  ///   A missing, unreadable or invalid configuration file, or a change that
  ///   would leave it invalid. The message is meant to be shown as-is.
  /// </summary>
  internal sealed class ConfigurationException : Exception
  {
    public ConfigurationException(string message)
      : base(message)
    {
    }

    public ConfigurationException(string message, Exception innerException)
      : base(message, innerException)
    {
    }
  }

  /// <summary>
  ///   Contents of ~/.sdlna/config.json.
  /// </summary>
  /// <remarks>
  ///   Read through the JSON configuration provider and written with
  ///   System.Text.Json. Collections are initialised empty rather than with
  ///   defaults: the configuration binder appends to an existing collection
  ///   instead of replacing it, so defaults here would leak into every file.
  /// </remarks>
  internal sealed class SdlnaConfiguration
  {
    public int Port { get; set; }

    public string Cache { get; set; }

    public string Editor { get; set; }

    public List<ServerConfiguration> Servers { get; set; } =
      new List<ServerConfiguration>();
  }

  internal sealed class ServerConfiguration
  {
    public string Name { get; set; }

    public List<string> Folders { get; set; } = new List<string>();

    public List<string> MediaTypes { get; set; } = new List<string>();

    public string SortOrder { get; set; }

    public string SortDirection { get; set; }

    public List<string> Views { get; set; } = new List<string>();

    public RestrictionsConfiguration Restrictions { get; set; } =
      new RestrictionsConfiguration();

    [JsonIgnore]
    public bool Descending => SortDirection == SortDirections.DESCENDING;
  }

  internal sealed class RestrictionsConfiguration
  {
    public List<string> Macs { get; set; } = new List<string>();

    public List<string> Ips { get; set; } = new List<string>();

    public List<string> UserAgents { get; set; } = new List<string>();

    [JsonIgnore]
    public bool IsEmpty =>
      Macs.Count == 0 && Ips.Count == 0 && UserAgents.Count == 0;
  }

  internal static class MediaTypeNames
  {
    public const string AUDIO = "audio";

    public const string IMAGES = "images";

    public const string VIDEO = "video";

    public static readonly string[] All = {VIDEO, AUDIO, IMAGES};

    /// <summary>
    ///   Accepts the singular "image" as well, since that is the spelling -t
    ///   has always used.
    /// </summary>
    public static bool TryNormalize(string value, out string name)
    {
      name = null;
      if (string.IsNullOrWhiteSpace(value)) {
        return false;
      }
      var v = value.Trim().ToLowerInvariant();
      if (v == "image") {
        v = IMAGES;
      }
      if (!All.Contains(v)) {
        return false;
      }
      name = v;
      return true;
    }

    public static DlnaMediaTypes ToDlnaMediaTypes(IEnumerable<string> names)
    {
      DlnaMediaTypes rv = 0;
      foreach (var name in names) {
        switch (name) {
        case VIDEO:
          rv |= DlnaMediaTypes.Video;
          break;
        case AUDIO:
          rv |= DlnaMediaTypes.Audio;
          break;
        case IMAGES:
          rv |= DlnaMediaTypes.Image;
          break;
        }
      }
      return rv;
    }
  }

  internal static class SortDirections
  {
    public const string ASCENDING = "asc";

    public const string DESCENDING = "desc";

    public static bool TryNormalize(string value, out string direction)
    {
      direction = value?.Trim().ToLowerInvariant();
      return direction == ASCENDING || direction == DESCENDING;
    }
  }

  internal static class ConfigurationValues
  {
    private static readonly Regex regMac = new Regex(
      "^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$", RegexOptions.Compiled);

    public static readonly StringComparer PathComparer =
      OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string DefaultSortOrder => "title";

    /// <summary>
    ///   Canonical form of a MAC address: upper case, colon separated. The
    ///   dash-separated form Windows displays is accepted too.
    /// </summary>
    public static bool TryNormalizeMac(string value, out string mac)
    {
      mac = value?.Trim().Replace('-', ':').ToUpperInvariant();
      return mac != null && regMac.IsMatch(mac);
    }

    public static bool TryNormalizeIp(string value, out string ip)
    {
      ip = null;
      IPAddress address;
      if (string.IsNullOrWhiteSpace(value) ||
          !IPAddress.TryParse(value.Trim(), out address)) {
        return false;
      }
      ip = address.ToString();
      return true;
    }

    public static bool TryNormalizeSortOrder(string value, out string order)
    {
      order = null;
      if (string.IsNullOrWhiteSpace(value)) {
        return false;
      }
      var match = ComparerRepository.ListItems().Keys.FirstOrDefault(
        k => k.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
      if (match == null) {
        return false;
      }
      order = match.ToLowerInvariant();
      return true;
    }

    public static IEnumerable<string> SortOrderNames =>
      ComparerRepository.ListItems().Keys
        .Select(k => k.ToLowerInvariant())
        .OrderBy(k => k, StringComparer.Ordinal);

    public static bool IsValidView(string view)
    {
      if (string.IsNullOrWhiteSpace(view)) {
        return false;
      }
      try {
        ViewRepository.Lookup(view.Trim());
        return true;
      }
      catch (RepositoryLookupException) {
        return false;
      }
    }

    public static string NormalizeFolder(string folder)
    {
      var full = Path.GetFullPath(ConfigurationStore.ExpandHome(folder.Trim()));
      return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    ///   The folder with symbolic links resolved in every component, for
    ///   deciding whether two spellings name the same folder. Path.GetFullPath
    ///   does not resolve links, so on macOS /tmp/x and /private/tmp/x would
    ///   otherwise count as different folders and be served twice. Only used
    ///   for comparison; folders are stored the way the user gave them.
    /// </summary>
    public static string CanonicalFolder(string folder)
    {
      var path = NormalizeFolder(folder);
      try {
        // Bounded: a link target may itself contain links.
        for (var i = 0; i < 16; ++i) {
          var resolved = ResolveLinks(path);
          if (PathComparer.Equals(resolved, path)) {
            break;
          }
          path = resolved;
        }
      }
      catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
        // Unreadable or dangling: compare what we have.
      }
      return path;
    }

    private static string ResolveLinks(string path)
    {
      var root = Path.GetPathRoot(path) ?? string.Empty;
      var current = root;
      var parts = path.Substring(root.Length).Split(
        new[] {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar},
        StringSplitOptions.RemoveEmptyEntries);
      foreach (var part in parts) {
        var next = Path.Combine(current, part);
        var info = new DirectoryInfo(next);
        if (info.LinkTarget != null) {
          var target = info.ResolveLinkTarget(true);
          if (target != null) {
            next = Path.TrimEndingDirectorySeparator(target.FullName);
          }
        }
        current = next;
      }
      return Path.TrimEndingDirectorySeparator(current);
    }
  }

  internal static class ConfigurationStore
  {
    private static readonly JsonSerializerOptions jsonOptions =
      new JsonSerializerOptions
      {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
      };

    public const string CACHE_NONE = "none";

    private static string HomeDirectory =>
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DirectoryPath =>
      Path.Combine(HomeDirectory, ".sdlna");

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    public static string DefaultCachePath =>
      Path.Combine(DirectoryPath, "cache.db");

    public static bool Exists => File.Exists(FilePath);

    public static string DefaultEditor =>
      OperatingSystem.IsWindows() ? "notepad" : "nano";

    public static SdlnaConfiguration CreateDefault()
    {
      return new SdlnaConfiguration
      {
        Port = 0,
        Cache = DefaultCachePath
      };
    }

    public static ServerConfiguration CreateServer(string name)
    {
      var rv = new ServerConfiguration
      {
        Name = name,
        SortOrder = ConfigurationValues.DefaultSortOrder,
        SortDirection = SortDirections.ASCENDING
      };
      rv.MediaTypes.AddRange(MediaTypeNames.All);
      return rv;
    }

    public static string ExpandHome(string path)
    {
      if (path == "~") {
        return HomeDirectory;
      }
      if (path.StartsWith("~/", StringComparison.Ordinal) ||
          path.StartsWith("~\\", StringComparison.Ordinal)) {
        return Path.Combine(HomeDirectory, path.Substring(2));
      }
      return path;
    }

    /// <summary>
    ///   The cache file to use, or null when the cache is switched off.
    /// </summary>
    public static FileInfo ResolveCache(SdlnaConfiguration config)
    {
      var cache = config.Cache?.Trim();
      if (string.IsNullOrEmpty(cache)) {
        return new FileInfo(DefaultCachePath);
      }
      if (cache.Equals(CACHE_NONE, StringComparison.OrdinalIgnoreCase)) {
        return null;
      }
      return new FileInfo(Path.GetFullPath(ExpandHome(cache)));
    }

    public static void EnsureDirectory()
    {
      if (Directory.Exists(DirectoryPath)) {
        return;
      }
      Directory.CreateDirectory(DirectoryPath);
      // Unix already treats a leading dot as hidden; Windows needs the
      // attribute.
      if (OperatingSystem.IsWindows()) {
        File.SetAttributes(
          DirectoryPath,
          File.GetAttributes(DirectoryPath) | FileAttributes.Hidden);
      }
    }

    /// <summary>
    ///   Loads and validates the configuration file.
    /// </summary>
    /// <returns>The configuration, or null when no file exists yet.</returns>
    public static SdlnaConfiguration Load()
    {
      if (!Exists) {
        return null;
      }
      SdlnaConfiguration config;
      IConfigurationRoot root = null;
      try {
        root = new ConfigurationBuilder()
          .AddJsonFile(FilePath, false, false)
          .Build();
        // Reject keys that match no setting, so that a typo in a
        // hand-edited file is reported instead of silently ignored.
        config = root.Get<SdlnaConfiguration>(
          o => o.ErrorOnUnknownConfiguration = true) ??
                 new SdlnaConfiguration();
      }
      catch (InvalidDataException ex) {
        throw new ConfigurationException(
          $"{FilePath} is not valid JSON: {DescribeParseError(ex)}", ex);
      }
      catch (InvalidOperationException ex) {
        throw new ConfigurationException(
          $"{FilePath} could not be read: {DescribeBindingError(ex)}", ex);
      }
      finally {
        (root as IDisposable)?.Dispose();
      }
      Normalize(config);
      Validate(config);
      return config;
    }

    public static void Save(SdlnaConfiguration config)
    {
      Validate(config);
      EnsureDirectory();
      var json = JsonSerializer.Serialize(config, jsonOptions);
      // Write beside the file and move it into place, so an interrupted
      // write cannot leave a truncated configuration behind.
      var temp = FilePath + ".tmp";
      File.WriteAllText(temp, json + Environment.NewLine, new UTF8Encoding(false));
      File.Move(temp, FilePath, true);
    }

    private static readonly Regex regUnknownKeys = new Regex(
      @"not found on the instance of [\w.]*?(?<type>\w+): (?<keys>.+?)\s*$",
      RegexOptions.Compiled);

    private static IEnumerable<string> Messages(Exception ex)
    {
      for (var e = ex; e != null; e = e.InnerException) {
        yield return e.Message.Trim();
      }
    }

    /// <summary>
    ///   The JSON provider's own message is generic ("Failed to load
    ///   configuration from file ..."); the parser's position is further in.
    /// </summary>
    private static string DescribeParseError(Exception ex)
    {
      var inner = Messages(ex).Skip(1).Distinct().ToList();
      return inner.Count == 0 ? ex.Message.Trim() : string.Join(" ", inner);
    }

    /// <summary>
    ///   Picks the binder message that names the offending key. For nested
    ///   objects the outermost message is a generic wrapper, so the whole
    ///   chain is searched. Unknown keys are reported as "unknown setting"
    ///   rather than exposing internal type names.
    /// </summary>
    private static string DescribeBindingError(Exception ex)
    {
      foreach (var message in Messages(ex)) {
        var match = regUnknownKeys.Match(message);
        if (!match.Success) {
          continue;
        }
        string where;
        switch (match.Groups["type"].Value) {
        case nameof(ServerConfiguration):
          where = "a server";
          break;
        case nameof(RestrictionsConfiguration):
          where = "a server's restrictions";
          break;
        default:
          where = "the top level";
          break;
        }
        return $"unknown setting {match.Groups["keys"].Value} in {where}";
      }
      return Messages(ex).FirstOrDefault(
        m => !m.StartsWith("'ErrorOnUnknownConfiguration' was set", StringComparison.Ordinal)) ??
             ex.Message.Trim();
    }

    /// <summary>
    ///   Fills in what a hand-written file may leave out, and canonicalises
    ///   spelling so that values compare reliably.
    /// </summary>
    private static void Normalize(SdlnaConfiguration config)
    {
      if (config.Servers == null) {
        config.Servers = new List<ServerConfiguration>();
      }
      foreach (var server in config.Servers.Where(s => s != null)) {
        server.Name = server.Name?.Trim();
        server.Folders = (server.Folders ?? new List<string>())
          .Where(f => !string.IsNullOrWhiteSpace(f))
          .ToList();
        server.Views = (server.Views ?? new List<string>())
          .Where(v => !string.IsNullOrWhiteSpace(v))
          .Select(v => v.Trim())
          .ToList();

        var types = new List<string>();
        foreach (var t in server.MediaTypes ?? new List<string>()) {
          string name;
          types.Add(MediaTypeNames.TryNormalize(t, out name) ? name : t);
        }
        server.MediaTypes = types.Distinct().ToList();

        if (string.IsNullOrWhiteSpace(server.SortOrder)) {
          server.SortOrder = ConfigurationValues.DefaultSortOrder;
        }
        else {
          string order;
          if (ConfigurationValues.TryNormalizeSortOrder(server.SortOrder, out order)) {
            server.SortOrder = order;
          }
        }
        if (string.IsNullOrWhiteSpace(server.SortDirection)) {
          server.SortDirection = SortDirections.ASCENDING;
        }
        else {
          string direction;
          if (SortDirections.TryNormalize(server.SortDirection, out direction)) {
            server.SortDirection = direction;
          }
        }

        var r = server.Restrictions ?? new RestrictionsConfiguration();
        r.Macs = Canonical(r.Macs, v =>
        {
          string mac;
          return ConfigurationValues.TryNormalizeMac(v, out mac) ? mac : v;
        });
        r.Ips = Canonical(r.Ips, v =>
        {
          string ip;
          return ConfigurationValues.TryNormalizeIp(v, out ip) ? ip : v;
        });
        r.UserAgents = (r.UserAgents ?? new List<string>())
          .Where(u => !string.IsNullOrWhiteSpace(u))
          .ToList();
        server.Restrictions = r;
      }
    }

    private static List<string> Canonical(IEnumerable<string> values,
      Func<string, string> normalize)
    {
      return (values ?? Enumerable.Empty<string>())
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(normalize)
        .Distinct()
        .ToList();
    }

    public static void Validate(SdlnaConfiguration config)
    {
      var errors = new List<string>();
      if (config.Port < 0 || config.Port > ushort.MaxValue) {
        errors.Add($"port must be between 0 and {ushort.MaxValue}, not {config.Port}");
      }

      var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (var i = 0; i < config.Servers.Count; ++i) {
        var server = config.Servers[i];
        if (server == null) {
          errors.Add($"server #{i + 1} is empty");
          continue;
        }
        var label = string.IsNullOrEmpty(server.Name)
          ? $"server #{i + 1}"
          : $"server \"{server.Name}\"";
        if (string.IsNullOrEmpty(server.Name)) {
          errors.Add($"{label} has no name");
        }
        else if (!names.Add(server.Name)) {
          errors.Add($"{label} is defined more than once");
        }
        if (server.Folders.Count == 0) {
          errors.Add($"{label} has no folders; it needs at least one");
        }
        if (server.MediaTypes.Count == 0) {
          errors.Add($"{label} has no mediaTypes; set at least one of {string.Join(", ", MediaTypeNames.All)}");
        }
        foreach (var t in server.MediaTypes.Where(t => !MediaTypeNames.All.Contains(t))) {
          errors.Add($"{label} has an unknown media type \"{t}\" (use {string.Join(", ", MediaTypeNames.All)})");
        }
        if (!ConfigurationValues.SortOrderNames.Contains(server.SortOrder)) {
          errors.Add($"{label} has an unknown sortOrder \"{server.SortOrder}\" (use {string.Join(", ", ConfigurationValues.SortOrderNames)})");
        }
        if (server.SortDirection != SortDirections.ASCENDING &&
            server.SortDirection != SortDirections.DESCENDING) {
          errors.Add($"{label} has an unknown sortDirection \"{server.SortDirection}\" (use asc or desc)");
        }
        foreach (var v in server.Views.Where(v => !ConfigurationValues.IsValidView(v))) {
          errors.Add($"{label} has an unknown view \"{v}\" (see sdlna --list-views)");
        }
        string ignored;
        foreach (var m in server.Restrictions.Macs.Where(m => !ConfigurationValues.TryNormalizeMac(m, out ignored))) {
          errors.Add($"{label} has an invalid MAC address \"{m}\" (expected six hex pairs, like 01:AF:BC:00:0A:FF)");
        }
        foreach (var ip in server.Restrictions.Ips.Where(ip => !ConfigurationValues.TryNormalizeIp(ip, out ignored))) {
          errors.Add($"{label} has an invalid IP address \"{ip}\"");
        }
      }

      if (errors.Count == 0) {
        return;
      }
      var sb = new StringBuilder();
      sb.AppendLine($"The configuration in {FilePath} is invalid:");
      foreach (var e in errors) {
        sb.AppendLine($"  - {e}");
      }
      sb.Append("Fix it with: sdlna --server config --edit");
      throw new ConfigurationException(sb.ToString());
    }
  }
}
