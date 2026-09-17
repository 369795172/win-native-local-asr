namespace WinLocalASR.Core.Settings;

/// <summary>
/// Applies autostart to the DERIVED state: the HKCU Run value
/// <c>WinLocalASR</c> = executable path. settings.json stays the SSOT
/// (<see cref="AppSettings.AutoStart"/>); callers that flip the setting write
/// BOTH (settings via <see cref="SettingsStore.Save"/>, Run key via this helper).
/// </summary>
public static class AutoStartHelper
{
    public const string RunValueName = "WinLocalASR";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(IRegistryKeyFactory registryFactory, bool enabled, string exePath)
    {
        using IRegistryKey runKey = registryFactory.OpenRunKey();
        if (enabled)
        {
            runKey.SetValue(RunValueName, exePath);
        }
        else
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled(IRegistryKeyFactory registryFactory)
    {
        using IRegistryKey runKey = registryFactory.OpenRunKey();
        return runKey.GetValue(RunValueName) is not null;
    }
}
