using System.Text.Json;
using System.Text.Json.Serialization;

namespace VeilDesk.Core.Settings;

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
    }

    public string Path => _path;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return Defaults();
                var json = File.ReadAllText(_path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? Defaults();
                SettingsValidator.ValidateAndClamp(settings);
                return settings;
            }
            catch (Exception)
            {
                TryQuarantineCorruptFile();
                return Defaults();
            }
        }
    }

    public AppSettings Defaults()
    {
        var s = new AppSettings();
        SettingsValidator.ValidateAndClamp(s);
        return s;
    }

    public void Save(AppSettings settings)
    {
        SettingsValidator.ValidateAndClamp(settings);
        lock (_gate)
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temp, json);
            // File.Replace is atomic on NTFS and keeps a recoverable backup.
            if (File.Exists(_path))
            {
                var backup = _path + ".bak";
                try { File.Replace(temp, _path, backup, ignoreMetadataErrors: true); }
                catch (IOException)
                {
                    File.Copy(temp, _path, overwrite: true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, _path);
            }
        }
    }

    public void ResetToDefaults()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var quarantine = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Move(_path, quarantine, overwrite: false);
        }
        catch { /* A corrupt settings file must never prevent startup. */ }
    }
}

public static class AppPaths
{
    public const string ProductName = "VeilDesk";
    private static string? _overrideRoot;

    /// <summary>Set by tests or by portable mode. Null uses LocalAppData.</summary>
    public static string? OverrideRoot
    {
        get => _overrideRoot;
        set => _overrideRoot = value;
    }

    public static string Root
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_overrideRoot)) return _overrideRoot!;
            var env = Environment.GetEnvironmentVariable("VEILDESK_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            var portable = System.IO.Path.Combine(AppContext.BaseDirectory, "portable.flag");
            if (File.Exists(portable)) return System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);
        }
    }

    public static string SettingsFile => System.IO.Path.Combine(Root, "settings.json");
    public static string SessionsFile => System.IO.Path.Combine(Root, "sessions.jsonl");
    public static string ExportsDirectory => System.IO.Path.Combine(Root, "exports");
    public static string PacksDirectory => System.IO.Path.Combine(Root, "packs");
    public static string LogsDirectory => System.IO.Path.Combine(Root, "logs");
    public static string ModelsDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "models");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(PacksDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
