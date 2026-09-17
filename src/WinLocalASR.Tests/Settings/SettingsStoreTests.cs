using WinLocalASR.Core.Settings;
using Xunit;

namespace WinLocalASR.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _baseDir =
        Path.Combine(Path.GetTempPath(), $"winlocalasr-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_baseDir, SettingsStore.SettingsDirName, SettingsStore.SettingsFileName);

    private SettingsStore CreateStore() => new(_baseDir);

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    [Fact]
    public void Save_then_Load_roundtrips_every_field()
    {
        SettingsStore store = CreateStore();
        var settings = new AppSettings
        {
            Hotkey = "Ctrl+Alt+D",
            DeviceId = "device-42",
            ContextPrompt = "meeting notes hotwords",
            RecordingLimitSeconds = 60,
            AutoStart = true,
            Configured = true,
        };

        store.Save(settings);
        AppSettings loaded = store.Load();

        Assert.Equal(settings, loaded);
    }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        AppSettings loaded = CreateStore().Load();

        Assert.Equal(new AppSettings(), loaded);
        Assert.Equal("Ctrl+Shift+Space", loaded.Hotkey);
        Assert.Null(loaded.DeviceId);
        Assert.Null(loaded.ContextPrompt);
        Assert.Equal(120, loaded.RecordingLimitSeconds);
        Assert.False(loaded.AutoStart);
        Assert.False(loaded.Configured);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_preserves_bak()
    {
        SettingsStore store = CreateStore();
        store.Save(new AppSettings { Hotkey = "Ctrl+Alt+D", Configured = true });
        Assert.False(File.Exists(store.BackupPath));

        string corruptBytes = "{ this is not json !!!";
        File.WriteAllText(SettingsPath, corruptBytes);

        AppSettings loaded = store.Load();

        Assert.Equal(new AppSettings(), loaded);                       // defaults
        Assert.True(File.Exists(store.BackupPath));                    // .bak exists
        Assert.Equal(corruptBytes, File.ReadAllText(store.BackupPath)); // .bak holds the corrupt bytes
        Assert.Equal(new AppSettings(), new SettingsStore(_baseDir).Load()); // main file rewritten clean
    }

    [Fact]
    public void Invalid_json_on_first_ever_load_returns_defaults_without_crashing()
    {
        SettingsStore store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "\0\0 truncated");

        AppSettings loaded = store.Load();

        Assert.Equal(new AppSettings(), loaded);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(-3, 10)]
    [InlineData(999, 120)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    public void Save_clamps_recording_limit_to_10_120(int saved, int expected)
    {
        SettingsStore store = CreateStore();

        store.Save(new AppSettings { RecordingLimitSeconds = saved });

        Assert.Equal(expected, store.Load().RecordingLimitSeconds);
    }

    [Fact]
    public void Load_clamps_out_of_range_limit_from_disk()
    {
        SettingsStore store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "hotkey": "Ctrl+Shift+Space", "recordingLimitSeconds": 400 }""");

        AppSettings loaded = store.Load();

        Assert.Equal(120, loaded.RecordingLimitSeconds);
    }

    [Fact]
    public void Save_is_atomic_no_tmp_file_left_behind()
    {
        SettingsStore store = CreateStore();

        store.Save(new AppSettings { Hotkey = "Ctrl+Alt+D" });

        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void MergePendingAutoStartFlag_flag_and_runkey_present_sets_autostart_true()
    {
        SettingsStore store = CreateStore();
        store.Save(new AppSettings { AutoStart = false, Configured = true });
        var registry = new FakeRegistryKeyFactory();
        registry.Values[AutoStartHelper.RunValueName] = @"C:\app\WinLocalASR.exe";
        File.WriteAllText(store.PendingAutoStartFlagPath, "");

        bool merged = store.MergePendingAutoStartFlag(registry);

        Assert.True(merged);
        Assert.True(store.Load().AutoStart);
        Assert.Equal(@"C:\app\WinLocalASR.exe", registry.Values[AutoStartHelper.RunValueName]); // run key untouched
        Assert.False(File.Exists(store.PendingAutoStartFlagPath)); // flag consumed
    }

    [Fact]
    public void MergePendingAutoStartFlag_flag_but_no_runkey_sets_autostart_false()
    {
        SettingsStore store = CreateStore();
        store.Save(new AppSettings { AutoStart = true }); // stale true from a previous uninstall state
        File.WriteAllText(store.PendingAutoStartFlagPath, "");

        bool merged = store.MergePendingAutoStartFlag(new FakeRegistryKeyFactory());

        Assert.True(merged);
        Assert.False(store.Load().AutoStart); // derived from run-key absence
        Assert.False(File.Exists(store.PendingAutoStartFlagPath));
    }

    [Fact]
    public void MergePendingAutoStartFlag_no_flag_is_a_noop()
    {
        SettingsStore store = CreateStore();
        store.Save(new AppSettings { Hotkey = "Ctrl+Alt+D" });
        string before = File.ReadAllText(SettingsPath);

        bool merged = store.MergePendingAutoStartFlag(new FakeRegistryKeyFactory());

        Assert.False(merged);
        Assert.Equal(before, File.ReadAllText(SettingsPath)); // settings.json untouched
    }

    [Fact]
    public void MergePendingAutoStartFlag_works_when_settings_file_does_not_exist_yet()
    {
        SettingsStore store = CreateStore();
        var registry = new FakeRegistryKeyFactory();
        registry.Values[AutoStartHelper.RunValueName] = @"C:\app\WinLocalASR.exe";
        Directory.CreateDirectory(Path.GetDirectoryName(store.PendingAutoStartFlagPath)!);
        File.WriteAllText(store.PendingAutoStartFlagPath, "");

        bool merged = store.MergePendingAutoStartFlag(registry);

        Assert.True(merged);
        AppSettings loaded = store.Load();
        Assert.True(loaded.AutoStart);
        Assert.Equal("Ctrl+Shift+Space", loaded.Hotkey); // other fields are defaults
        Assert.False(File.Exists(store.PendingAutoStartFlagPath));
    }
}
