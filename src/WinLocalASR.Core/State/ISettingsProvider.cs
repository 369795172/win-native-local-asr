using WinLocalASR.Core.Settings;

namespace WinLocalASR.Core.State;

/// <summary>
/// Settings access seam (Swift AppState reads SettingsStore statics; the port injects a
/// loader so tests control the snapshot without touching %APPDATA%).
/// </summary>
public interface ISettingsProvider
{
    AppSettings Load();
}
