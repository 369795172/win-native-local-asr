using System.Collections;
using System.Globalization;
using System.Resources;
using WinLocalASR.Resources;
using Xunit;

namespace WinLocalASR.Tests.Resources;

/// <summary>
/// Bilingual resource integrity: identical key sets in the English neutral
/// resources and the zh-Hans satellite (no missing translations), non-empty
/// values everywhere, and CurrentUICulture-driven selection returning the
/// Chinese strings. Runs cross-platform (pure ResourceManager).
/// </summary>
public class StringsResourceTests
{
    private static ResourceManager NewManager() =>
        new("WinLocalASR.Resources.Strings", typeof(Strings).Assembly);

    [Fact]
    public void English_and_zh_Hans_resource_sets_have_identical_keys()
    {
        ResourceManager manager = NewManager();
        using ResourceSet? en = manager.GetResourceSet(
            CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        using ResourceSet? zh = manager.GetResourceSet(
            new CultureInfo("zh-Hans"), createIfNotExists: true, tryParents: false);

        Assert.NotNull(en);
        Assert.NotNull(zh);

        HashSet<string> enKeys = KeysOf(en!);
        HashSet<string> zhKeys = KeysOf(zh!);

        Assert.NotEmpty(enKeys);
        Assert.Empty(enKeys.Except(zhKeys));
        Assert.Empty(zhKeys.Except(enKeys));
    }

    [Fact]
    public void Every_value_is_non_empty_in_both_cultures()
    {
        ResourceManager manager = NewManager();
        using ResourceSet? en = manager.GetResourceSet(
            CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        using ResourceSet? zh = manager.GetResourceSet(
            new CultureInfo("zh-Hans"), createIfNotExists: true, tryParents: false);

        Assert.All(EntriesOf(en!), e => Assert.False(string.IsNullOrWhiteSpace(e.Value)));
        Assert.All(EntriesOf(zh!), e => Assert.False(string.IsNullOrWhiteSpace(e.Value)));
    }

    [Fact]
    public void Zh_Hans_ui_culture_returns_the_chinese_strings()
    {
        (CultureInfo currentCulture, CultureInfo currentUICulture) =
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("zh-Hans");
            CultureInfo.CurrentUICulture = new CultureInfo("zh-Hans");

            Assert.Equal("WinLocalASR 设置", Strings.Settings_Title);
            Assert.Equal("录音上限须在 10-120 秒之间，已自动调整。", Strings.Settings_LimitClamped);
            Assert.Equal("应用", Strings.Settings_Apply);
        }
        finally
        {
            CultureInfo.CurrentCulture = currentCulture;
            CultureInfo.CurrentUICulture = currentUICulture;
        }
    }

    [Fact]
    public void Invariant_ui_culture_returns_the_english_strings()
    {
        (CultureInfo currentCulture, CultureInfo currentUICulture) =
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            Assert.Equal("WinLocalASR Settings", Strings.Settings_Title);
            Assert.Equal("Recording limit must be 10-120 seconds; the value was adjusted.", Strings.Settings_LimitClamped);
        }
        finally
        {
            CultureInfo.CurrentCulture = currentCulture;
            CultureInfo.CurrentUICulture = currentUICulture;
        }
    }

    [Fact]
    public void Every_public_string_property_resolves_in_both_cultures()
    {
        (CultureInfo currentUICulture, var properties) = (CultureInfo.CurrentUICulture,
            typeof(Strings).GetProperties().Where(p => p.PropertyType == typeof(string)).ToArray());
        try
        {
            Assert.NotEmpty(properties);

            CultureInfo.CurrentUICulture = new CultureInfo("zh-Hans");
            string[] zh = properties.Select(p => (string)p.GetValue(null)!).ToArray();

            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            string[] en = properties.Select(p => (string)p.GetValue(null)!).ToArray();

            for (int i = 0; i < properties.Length; i++)
            {
                Assert.False(string.IsNullOrEmpty(en[i]), $"{properties[i].Name} missing in neutral resources");
                Assert.False(string.IsNullOrEmpty(zh[i]), $"{properties[i].Name} missing in zh-Hans");
            }

            Assert.Contains(properties.Select((p, i) => (p, i)), t => en[t.i] != zh[t.i]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = currentUICulture;
        }
    }

    private static HashSet<string> KeysOf(ResourceSet set) =>
        EntriesOf(set).Select(e => e.Key).ToHashSet();

    private static List<KeyValuePair<string, string>> EntriesOf(ResourceSet set) =>
        set.Cast<DictionaryEntry>().Select(e => new KeyValuePair<string, string>((string)e.Key, (string)e.Value!)).ToList();
}
