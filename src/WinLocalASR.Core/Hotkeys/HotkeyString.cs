namespace WinLocalASR.Core.Hotkeys;

/// <summary>
/// Serializes/parses the settings hotkey string format, e.g.
/// <c>"Ctrl+Shift+Space"</c> — modifier names joined with '+' followed by the
/// key name. Pure cross-platform logic: parsing is case-insensitive, tolerant
/// of whitespace and modifier order, accepts the aliases Control/Windows, and
/// every unsupported input produces a descriptive error (never a silent
/// fallback). <see cref="Serialize"/> always emits the canonical order
/// Ctrl, Alt, Shift, Win.
/// </summary>
public static class HotkeyString
{
    public const char Separator = '+';

    private static readonly (string Canonical, string[] Aliases)[] ModifierTokens =
    {
        ("Ctrl", new[] { "Control" }),
        ("Alt", Array.Empty<string>()),
        ("Shift", Array.Empty<string>()),
        ("Win", new[] { "Windows" }),
    };

    public static string Serialize(HotkeyModifiers modifiers, HotkeyKey key)
    {
        var parts = new List<string>(5);
        foreach ((string canonical, _) in ModifierTokens)
        {
            if (modifiers.HasFlag(ModifierFromName(canonical)))
            {
                parts.Add(canonical);
            }
        }

        parts.Add(key.ToString());
        return string.Join(Separator, parts);
    }

    public static string Serialize(HotkeyCombination combination) =>
        Serialize(combination.Modifiers, combination.Key);

    /// <summary>Throws <see cref="FormatException"/> with a descriptive message on any invalid input.</summary>
    public static HotkeyCombination Parse(string text) =>
        TryParseCore(text, out HotkeyCombination combination, out string? error)
            ? combination
            : throw new FormatException(error);

    public static bool TryParse(string? text, out HotkeyCombination combination)
    {
        bool ok = TryParseCore(text, out HotkeyCombination parsed, out _);
        combination = parsed;
        return ok;
    }

    private static bool TryParseCore(string? text, out HotkeyCombination combination, out string? error)
    {
        combination = new HotkeyCombination(HotkeyModifiers.None, HotkeyKey.Space);

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Hotkey string is empty.";
            return false;
        }

        string[] tokens = text.Split(
            Separator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = $"'{text}' does not contain any key names.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            string token = tokens[i];
            if (!TryMapModifier(token, out HotkeyModifiers mapped))
            {
                error = $"'{token}' is not a valid modifier; expected Ctrl, Alt, Shift or Win before the key name.";
                return false;
            }

            if (modifiers.HasFlag(mapped))
            {
                error = $"Modifier '{token}' appears more than once.";
                return false;
            }

            modifiers |= mapped;
        }

        string keyToken = tokens[^1];
        if (TryMapModifier(keyToken, out _))
        {
            error = $"Missing key: '{text}' ends with a modifier (a hotkey looks like 'Ctrl+A').";
            return false;
        }

        if (!Enum.TryParse(keyToken, ignoreCase: true, out HotkeyKey key) || !Enum.IsDefined(key))
        {
            error = $"'{keyToken}' is not a supported key name.";
            return false;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "A hotkey must include at least one modifier (Ctrl, Alt, Shift, Win).";
            return false;
        }

        combination = new HotkeyCombination(modifiers, key);
        error = null;
        return true;
    }

    private static bool TryMapModifier(string token, out HotkeyModifiers modifier)
    {
        foreach ((string canonical, string[] aliases) in ModifierTokens)
        {
            if (token.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                || Array.Exists(aliases, a => token.Equals(a, StringComparison.OrdinalIgnoreCase)))
            {
                modifier = ModifierFromName(canonical);
                return true;
            }
        }

        modifier = HotkeyModifiers.None;
        return false;
    }

    private static HotkeyModifiers ModifierFromName(string canonical) => canonical switch
    {
        "Ctrl" => HotkeyModifiers.Control,
        "Alt" => HotkeyModifiers.Alt,
        "Shift" => HotkeyModifiers.Shift,
        "Win" => HotkeyModifiers.Windows,
        _ => throw new ArgumentOutOfRangeException(nameof(canonical)),
    };
}
