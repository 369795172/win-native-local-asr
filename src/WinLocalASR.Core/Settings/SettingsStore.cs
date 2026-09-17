using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinLocalASR.Core.Settings;

/// <summary>
/// JSON-backed settings store at <c>&lt;baseDir&gt;\WinLocalASR\settings.json</c>
/// (default baseDir = <c>%APPDATA%</c>; tests inject a temp directory).
///
/// Write path is atomic (tmp file + move) so a crashed save never destroys the
/// previous file. Read path falls back to defaults on corrupt JSON, preserving
/// the corrupt bytes as <c>settings.json.bak</c> before rewriting defaults.
///
/// Autostart SSOT: settings.json owns <see cref="AppSettings.AutoStart"/>; the
/// HKCU Run key is derived. An installer that must set autostart without editing
/// this file writes the Run key plus a pending flag file and calls
/// <see cref="MergePendingAutoStartFlag"/> on next launch.
/// </summary>
public sealed class SettingsStore
{
    public const string SettingsDirName = "WinLocalASR";
    public const string SettingsFileName = "settings.json";
    public const string PendingAutoStartFlagFileName = "autostart-pending.flag";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly string _pendingAutoStartFlagPath;

    public SettingsStore(string? baseDirectory = null)
    {
        string baseDir = baseDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsDirectory = Path.Combine(baseDir, SettingsDirName);
        _settingsPath = Path.Combine(_settingsDirectory, SettingsFileName);
        _backupPath = _settingsPath + ".bak";
        _pendingAutoStartFlagPath = Path.Combine(_settingsDirectory, PendingAutoStartFlagFileName);
    }

    public string SettingsPath => _settingsPath;
    public string BackupPath => _backupPath;
    public string PendingAutoStartFlagPath => _pendingAutoStartFlagPath;

    /// <summary>
    /// Loads settings; missing file or invalid JSON yields defaults. Corrupt bytes
    /// are preserved as .bak before the file is rewritten with clean defaults.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath))
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(_settingsPath, _backupPath, overwrite: true);
            AppSettings defaults = new();
            Save(defaults);
            return defaults;
        }

        return AppSettings.Clamp(settings);
    }

    /// <summary>Atomically persists settings (clamped), creating the directory on demand.</summary>
    public void Save(AppSettings settings)
    {
        AppSettings clamped = AppSettings.Clamp(settings);
        Directory.CreateDirectory(_settingsDirectory);

        string tmpPath = _settingsPath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(clamped, SerializerOptions));
        File.Move(tmpPath, _settingsPath, overwrite: true);
    }

    /// <summary>
    /// Installer bridge: merges a pending autostart request (flag file + Run key
    /// written by the installer, which never edits settings.json) into the SSOT.
    /// If the flag file exists, loads current settings, sets AutoStart from the
    /// Run key presence (via the injected registry), read-modify-writes
    /// settings.json, and deletes the flag.
    /// </summary>
    /// <returns>True if a pending flag was found and merged; false = no-op.</returns>
    public bool MergePendingAutoStartFlag(IRegistryKeyFactory registryFactory)
    {
        if (!File.Exists(_pendingAutoStartFlagPath))
        {
            return false;
        }

        AppSettings current = Load();
        bool runKeyPresent;
        using (IRegistryKey runKey = registryFactory.OpenRunKey())
        {
            runKeyPresent = runKey.GetValue(AutoStartHelper.RunValueName) is not null;
        }

        Save(current with { AutoStart = runKeyPresent });
        File.Delete(_pendingAutoStartFlagPath);
        return true;
    }
}
