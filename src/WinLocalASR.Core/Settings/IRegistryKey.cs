namespace WinLocalASR.Core.Settings;

/// <summary>
/// Minimal key/value abstraction over a registry key, so autostart logic is
/// unit-testable with fakes. The real adapter wraps
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
public interface IRegistryKey : IDisposable
{
    object? GetValue(string name);

    void SetValue(string name, object value);

    void DeleteValue(string name, bool throwOnMissingValue);
}

public interface IRegistryKeyFactory
{
    /// <summary>Opens the HKCU Run key (writable, creates if missing).</summary>
    IRegistryKey OpenRunKey();
}
