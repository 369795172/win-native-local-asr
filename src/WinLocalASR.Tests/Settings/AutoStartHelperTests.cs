using WinLocalASR.Core.Settings;
using Xunit;

namespace WinLocalASR.Tests.Settings;

public class AutoStartHelperTests
{
    [Fact]
    public void Apply_enabled_writes_run_value()
    {
        var registry = new FakeRegistryKeyFactory();

        AutoStartHelper.Apply(registry, enabled: true, @"C:\app\WinLocalASR.exe");

        Assert.Equal(@"C:\app\WinLocalASR.exe", registry.Values[AutoStartHelper.RunValueName]);
        Assert.True(AutoStartHelper.IsEnabled(registry));
    }

    [Fact]
    public void Apply_disabled_deletes_run_value_without_throwing_when_missing()
    {
        var registry = new FakeRegistryKeyFactory();

        AutoStartHelper.Apply(registry, enabled: false, @"C:\app\WinLocalASR.exe");

        Assert.DoesNotContain(AutoStartHelper.RunValueName, registry.Values.Keys);
        Assert.False(AutoStartHelper.IsEnabled(registry));
    }

    [Fact]
    public void Apply_disabled_removes_existing_run_value()
    {
        var registry = new FakeRegistryKeyFactory();
        registry.Values[AutoStartHelper.RunValueName] = @"C:\old\WinLocalASR.exe";

        AutoStartHelper.Apply(registry, enabled: false, @"C:\app\WinLocalASR.exe");

        Assert.DoesNotContain(AutoStartHelper.RunValueName, registry.Values.Keys);
        Assert.True(registry.DeleteCalled); // went through DeleteValue, not SetValue overwrite
    }

    [SkippableFact]
    public void Real_factory_rejects_non_windows_platforms()
    {
        Skip.If(OperatingSystem.IsWindows(), "real HKCU registry exists on Windows");

        Assert.Throws<PlatformNotSupportedException>(() => new RegistryKeyFactory().OpenRunKey());
    }
}

/// <summary>In-memory registry fake: unit tests never touch the real HKCU hive.</summary>
internal sealed class FakeRegistryKeyFactory : IRegistryKeyFactory
{
    public Dictionary<string, object> Values { get; } = new();

    public bool DeleteCalled { get; private set; }

    public IRegistryKey OpenRunKey() => new FakeRegistryKey(this);

    private sealed class FakeRegistryKey(FakeRegistryKeyFactory owner) : IRegistryKey
    {
        public object? GetValue(string name) =>
            owner.Values.TryGetValue(name, out object? value) ? value : null;

        public void SetValue(string name, object value) => owner.Values[name] = value;

        public void DeleteValue(string name, bool throwOnMissingValue)
        {
            if (!owner.Values.Remove(name) && throwOnMissingValue)
            {
                throw new ArgumentException($"value '{name}' does not exist");
            }

            owner.DeleteCalled = true;
        }

        public void Dispose()
        {
        }
    }
}
