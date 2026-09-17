using System.Reflection;
using WinLocalASR.Core.Shell;

namespace WinLocalASR.App;

/// <summary>
/// Loads the four phase icons embedded from Assets\tray-*.ico. Any load failure degrades
/// to the default system icon with a logged warning — a missing icon must never take the
/// shell down (GUI-degrade philosophy applies to every UI construct).
/// </summary>
internal static class TrayIconSet
{
    private static readonly (TrayIconKind Kind, string Suffix)[] Entries =
    {
        (TrayIconKind.Idle, "idle"),
        (TrayIconKind.Recording, "recording"),
        (TrayIconKind.Processing, "processing"),
        (TrayIconKind.Error, "error"),
    };

    public static IReadOnlyDictionary<TrayIconKind, Icon> Load(IShellLog log)
    {
        Assembly assembly = typeof(TrayShell).Assembly;
        Dictionary<TrayIconKind, Icon> icons = new();
        foreach ((TrayIconKind kind, string suffix) in Entries)
        {
            icons[kind] = LoadIcon(assembly, suffix, log);
        }

        return icons;
    }

    private static Icon LoadIcon(Assembly assembly, string suffix, IShellLog log)
    {
        string resourceName = $"WinLocalASR.App.Assets.tray-{suffix}.ico";
        try
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName) ?? FindBySuffix(assembly, suffix);
            if (stream is not null)
            {
                return new Icon(stream);
            }

            log.Warn($"tray icon resource '{resourceName}' not found; using the default system icon");
        }
        catch (Exception ex)
        {
            log.Warn($"tray icon '{resourceName}' failed to load ({ex.Message}); using the default system icon");
        }

        return SystemIcons.Application;
    }

    private static Stream? FindBySuffix(Assembly assembly, string suffix) =>
        assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith($"tray-{suffix}.ico", StringComparison.OrdinalIgnoreCase))
            .Select(assembly.GetManifestResourceStream)
            .FirstOrDefault(stream => stream is not null);
}
