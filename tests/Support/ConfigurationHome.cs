using System;
using System.IO;
using System.Text.Json;

namespace NMaier.SimpleDlna.Tests.Support
{
  /// <summary>
  ///   Points the configuration store at a scratch home directory for the
  ///   lifetime of a test, so the real ~/.sdlna is never touched. Use only
  ///   from tests in <see cref="ProcessStateCollection" />.
  /// </summary>
  internal sealed class ConfigurationHome : IDisposable
  {
    private readonly TempDirectory home = new TempDirectory();

    public ConfigurationHome()
    {
      ConfigurationStore.HomeDirectoryOverride = home.Path;
    }

    public string Path => home.Path;

    public string ConfigPath => ConfigurationStore.FilePath;

    public void Dispose()
    {
      ConfigurationStore.HomeDirectoryOverride = null;
      home.Dispose();
    }

    public void WriteConfig(string json)
    {
      Directory.CreateDirectory(ConfigurationStore.DirectoryPath);
      File.WriteAllText(ConfigPath, json);
    }

    public JsonDocument ReadConfig()
    {
      // Parsed with default options: strict JSON, no comments or trailing commas.
      return JsonDocument.Parse(File.ReadAllText(ConfigPath));
    }

    public byte[] ConfigBytes()
    {
      return File.ReadAllBytes(ConfigPath);
    }

    /// <summary>
    ///   A media folder inside the scratch home.
    /// </summary>
    public string Folder(string name)
    {
      return Directory.CreateDirectory(System.IO.Path.Combine(home.Path, name)).FullName;
    }
  }
}
