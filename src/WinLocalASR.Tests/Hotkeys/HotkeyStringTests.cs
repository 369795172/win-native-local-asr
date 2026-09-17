using WinLocalASR.Core.Hotkeys;
using WinLocalASR.Core.Settings;
using Xunit;

namespace WinLocalASR.Tests.Hotkeys;

public class HotkeyStringTests
{
    [Fact]
    public void Parses_the_default_hotkey()
    {
        HotkeyCombination combo = HotkeyString.Parse("Ctrl+Shift+Space");
        Assert.Equal(new HotkeyCombination(HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyKey.Space), combo);
        Assert.Equal(AppSettings.DefaultHotkey, HotkeyString.Serialize(combo));
    }

    public static TheoryData<HotkeyModifiers, HotkeyKey, string> RoundtripCases => new()
    {
        { HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyKey.Space, "Ctrl+Shift+Space" },
        { HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyKey.F5, "Ctrl+Alt+F5" },
        { HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, HotkeyKey.Z, "Ctrl+Alt+Shift+Win+Z" },
        { HotkeyModifiers.Alt, HotkeyKey.D9, "Alt+D9" },
        { HotkeyModifiers.Windows, HotkeyKey.F12, "Win+F12" },
        { HotkeyModifiers.Control, HotkeyKey.NumPad0, "Ctrl+NumPad0" },
        { HotkeyModifiers.Control, HotkeyKey.Multiply, "Ctrl+Multiply" },
        { HotkeyModifiers.Shift, HotkeyKey.PageUp, "Shift+PageUp" },
        { HotkeyModifiers.Control, HotkeyKey.Escape, "Ctrl+Escape" },
        { HotkeyModifiers.Alt, HotkeyKey.Enter, "Alt+Enter" },
    };

    [Theory]
    [MemberData(nameof(RoundtripCases))]
    public void Serialize_then_Parse_roundtrips(HotkeyModifiers modifiers, HotkeyKey key, string expected)
    {
        string serialized = HotkeyString.Serialize(modifiers, key);
        Assert.Equal(expected, serialized);
        Assert.Equal(new HotkeyCombination(modifiers, key), HotkeyString.Parse(serialized));
    }

    [Theory]
    [InlineData("ctrl+shift+space")]
    [InlineData("CTRL+SHIFT+SPACE")]
    [InlineData(" Ctrl + Shift + Space ")]
    [InlineData("cOnTrOl+aLt+DeLeTe")]
    [InlineData("control+alt+delete")]
    [InlineData("windows+l")]
    public void Parsing_is_case_insensitive_and_whitespace_tolerant(string text)
    {
        HotkeyCombination combo = HotkeyString.Parse(text);
        Assert.True(HotkeyString.TryParse(HotkeyString.Serialize(combo), out _));
    }

    [Fact]
    public void Alias_and_case_insensitive_parse_matches_canonical_combination()
    {
        Assert.Equal(
            new HotkeyCombination(HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyKey.Delete),
            HotkeyString.Parse("Control+Alt+Delete"));
        Assert.Equal(
            new HotkeyCombination(HotkeyModifiers.Windows, HotkeyKey.L),
            HotkeyString.Parse("Windows+L"));
    }

    [Fact]
    public void Parse_accepts_any_modifier_order_but_Serialize_is_canonical()
    {
        HotkeyCombination combo = HotkeyString.Parse("Shift+Ctrl+Space");
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, combo.Modifiers);
        Assert.Equal("Ctrl+Shift+Space", HotkeyString.Serialize(combo));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("+++", "does not contain")]
    [InlineData("Ctrl", "ends with a modifier")]
    [InlineData("Ctrl+", "ends with a modifier")]
    [InlineData("Foo+A", "'Foo' is not a valid modifier")]
    [InlineData("Ctrl+Bar", "'Bar' is not a supported key name")]
    [InlineData("A", "at least one modifier")]
    [InlineData("Space", "at least one modifier")]
    [InlineData("Ctrl+Ctrl+A", "appears more than once")]
    [InlineData("Ctrl+Control+A", "appears more than once")]
    [InlineData("Ctrl+Shift+Shift+Space", "appears more than once")]
    public void Invalid_input_throws_a_descriptive_error(string text, string expectedFragment)
    {
        FormatException ex = Assert.Throws<FormatException>(() => HotkeyString.Parse(text));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_returns_false_for_invalid_input_without_throwing()
    {
        Assert.False(HotkeyString.TryParse("Nonsense", out _));
        Assert.False(HotkeyString.TryParse(null, out _));
        Assert.False(HotkeyString.TryParse("", out _));
    }

    [Fact]
    public void Modifier_flag_values_match_RegisterHotKey_MOD_constants()
    {
        // Contract for the hotkey manager: cast straight to RegisterHotKey's fsModifiers.
        Assert.Equal(0x1u, (uint)HotkeyModifiers.Alt);       // MOD_ALT
        Assert.Equal(0x2u, (uint)HotkeyModifiers.Control);   // MOD_CONTROL
        Assert.Equal(0x4u, (uint)HotkeyModifiers.Shift);     // MOD_SHIFT
        Assert.Equal(0x8u, (uint)HotkeyModifiers.Windows);   // MOD_WIN
    }

    [Theory]
    [InlineData(HotkeyKey.Backspace, 0x08)]
    [InlineData(HotkeyKey.Enter, 0x0D)]
    [InlineData(HotkeyKey.Escape, 0x1B)]
    [InlineData(HotkeyKey.Space, 0x20)]
    [InlineData(HotkeyKey.Left, 0x25)]
    [InlineData(HotkeyKey.Insert, 0x2D)]
    [InlineData(HotkeyKey.D0, 0x30)]
    [InlineData(HotkeyKey.D9, 0x39)]
    [InlineData(HotkeyKey.A, 0x41)]
    [InlineData(HotkeyKey.Z, 0x5A)]
    [InlineData(HotkeyKey.NumPad0, 0x60)]
    [InlineData(HotkeyKey.NumPad9, 0x69)]
    [InlineData(HotkeyKey.Divide, 0x6F)]
    [InlineData(HotkeyKey.F1, 0x70)]
    [InlineData(HotkeyKey.F12, 0x7B)]
    public void Key_values_are_literal_windows_virtual_key_codes(HotkeyKey key, int vk)
    {
        Assert.Equal(vk, (int)key);
    }

    [Fact]
    public void Modifier_key_vks_are_not_capturable_keys()
    {
        // Bare-modifier KeyDowns (Shift/Ctrl/Alt/Win keys themselves) must not
        // parse as hotkey keys, so the capture box keeps waiting.
        int[] modifierVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };
        Assert.All(modifierVks, vk => Assert.False(Enum.IsDefined(typeof(HotkeyKey), vk)));
        Assert.False(Enum.IsDefined(typeof(HotkeyKey), 0x09)); // Tab navigates, not captured
    }
}
