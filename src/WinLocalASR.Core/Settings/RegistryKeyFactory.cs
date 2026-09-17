using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinLocalASR.Core.Settings;

/// <summary>
/// Real HKCU-backed registry factory (Windows only; throws on other platforms —
/// unit tests use fakes, real-registry verification is a Windows runtime item).
/// </summary>
public sealed class RegistryKeyFactory : IRegistryKeyFactory
{
    public IRegistryKey OpenRunKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The real HKCU Run key exists only on Windows; tests must use a fake.");
        }

        RegistryKey key = Registry.CurrentUser.CreateSubKey(AutoStartHelper.RunKeyPath, writable: true);
        return new RegistryKeyAdapter(key);
    }

    [SupportedOSPlatform("windows")]
    private sealed class RegistryKeyAdapter(RegistryKey key) : IRegistryKey
    {
        public object? GetValue(string name) => key.GetValue(name);

        public void SetValue(string name, object value) => key.SetValue(name, value);

        public void DeleteValue(string name, bool throwOnMissingValue) =>
            key.DeleteValue(name, throwOnMissingValue);

        public void Dispose() => key.Dispose();
    }
}
