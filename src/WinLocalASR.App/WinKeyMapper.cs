using System.Runtime.InteropServices;
using WinLocalASR.Core.Hotkeys;

namespace WinLocalASR.App;

/// <summary>
/// WinForms Keys → HotkeyModifiers/HotkeyKey mapping for the capture box.
/// KeyEventArgs.Modifiers never reports the Windows key, so its live state is
/// read via GetAsyncKeyState. VK values match HotkeyKey 1:1, so the mapping
/// is a cast plus a defined-ness check (bare-modifier and unsupported keys
/// fall through and are swallowed by the capture box).
/// </summary>
internal static partial class WinKeyMapper
{
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    internal static HotkeyModifiers MapModifiers(System.Windows.Forms.Keys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if (modifiers.HasFlag(System.Windows.Forms.Keys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(System.Windows.Forms.Keys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(System.Windows.Forms.Keys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (IsWinPressed())
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    internal static bool IsWinPressed() =>
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    internal static bool TryMapKey(System.Windows.Forms.Keys keyCode, out HotkeyKey key)
    {
        int value = (int)keyCode;
        if (Enum.IsDefined(typeof(HotkeyKey), value))
        {
            key = (HotkeyKey)value;
            return true;
        }

        key = default;
        return false;
    }
}
