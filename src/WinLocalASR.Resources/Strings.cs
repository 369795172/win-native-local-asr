using System.Resources;

namespace WinLocalASR.Resources;

/// <summary>
/// Bilingual UI strings (English neutral resources + zh-Hans satellite
/// assembly), selected automatically via CurrentUICulture. Standard resx
/// naming so single-file publish embeds the satellites via
/// IncludeSatelliteAssembliesInSingleFile without special handling.
/// Consumers: SettingsDialog today; the tray shell and HUD later.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("WinLocalASR.Resources.Strings", typeof(Strings).Assembly);

    public static string Settings_Title => GetString();

    public static string Settings_HotkeyLabel => GetString();

    public static string Settings_HotkeyHint => GetString();

    public static string Settings_HotkeyCaptureActive => GetString();

    public static string Settings_HotkeyCaptureCancelled => GetString();

    public static string Settings_HotkeyNeedsModifier => GetString();

    public static string Settings_HotkeyInvalidReset => GetString();

    public static string Settings_DeviceLabel => GetString();

    public static string Settings_SystemDefaultDevice => GetString();

    public static string Settings_RefreshDevices => GetString();

    public static string Settings_ContextPromptLabel => GetString();

    public static string Settings_ContextPromptPlaceholder => GetString();

    public static string Settings_ContextPromptHint => GetString();

    public static string Settings_LimitLabel => GetString();

    public static string Settings_LimitHint => GetString();

    public static string Settings_LimitClamped => GetString();

    public static string Settings_AutoStart => GetString();

    public static string Settings_Apply => GetString();

    public static string Settings_Close => GetString();

    public static string Settings_Applied => GetString();

    public static string Settings_ApplyFailed => GetString();

    private static string GetString([System.Runtime.CompilerServices.CallerMemberName] string key = "") =>
        Manager.GetString(key) ?? key;
}
