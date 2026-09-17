using Microsoft.Win32;
using WinLocalASR.Core.Audio;
using WinLocalASR.Core.Settings;
using WinLocalASR.Core.SettingsUi;
using Xunit;

namespace WinLocalASR.Tests.SettingsUi;

/// <summary>
/// QA+ real-registry verification (windows-latest / real machine only):
/// checking autostart in the presenter must create the real HKCU Run value
/// <c>WinLocalASR</c> with the exe path; unchecking must remove it. The
/// finally block deletes the value even though CI runners are ephemeral.
/// </summary>
public class RealRegistryAutoStartTests
{
    // CA1416: raw Microsoft.Win32 access is guarded at runtime by Skip.If(!IsWindows).
#pragma warning disable CA1416
    [SkippableFact]
    public void Autostart_checked_then_unchecked_writes_and_removes_the_real_run_key_value()
    {
        Skip.If(!OperatingSystem.IsWindows());

        string baseDir = Path.Combine(
            Path.GetTempPath(), "winlocalasr-realreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        const string exePath = @"C:\test-path\WinLocalASR.exe";
        RegistryKey? runKey = null;
        try
        {
            var view = new MinimalView();
            var presenter = new SettingsDialogPresenter(
                new SettingsStore(baseDir),
                new RegistryKeyFactory(),
                exePath,
                Array.Empty<AudioDeviceInfo>,
                view,
                applyWithoutReload: null);

            presenter.Load();
            presenter.AutoStartChanged(true);
            Assert.True(presenter.Apply());

            runKey = Registry.CurrentUser.OpenSubKey(AutoStartHelper.RunKeyPath, writable: true);
            Assert.NotNull(runKey);
            Assert.NotNull(runKey!.GetValue(AutoStartHelper.RunValueName));
            Assert.Equal(exePath, (string?)runKey.GetValue(AutoStartHelper.RunValueName));

            presenter.AutoStartChanged(false);
            Assert.True(presenter.Apply());

            Assert.Null(runKey.GetValue(AutoStartHelper.RunValueName));
        }
        finally
        {
            runKey?.DeleteValue(AutoStartHelper.RunValueName, throwOnMissingValue: false);
            runKey?.Dispose();
            try
            {
                Directory.Delete(baseDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class MinimalView : ISettingsDialogView
    {
        public void SetHotkey(string hotkey)
        {
        }

        public void SetContextPrompt(string contextPrompt)
        {
        }

        public void SetRecordingLimit(int seconds)
        {
        }

        public void SetAutoStart(bool enabled)
        {
        }

        public void SetDevices(IReadOnlyList<AudioDeviceInfo> devices, string? selectedDeviceId)
        {
        }

        public void ShowHint(SettingsHint hint, string? detail = null)
        {
        }

        public void ClearHint()
        {
        }
    }
}
