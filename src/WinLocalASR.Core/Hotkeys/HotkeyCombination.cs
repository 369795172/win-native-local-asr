namespace WinLocalASR.Core.Hotkeys;

/// <summary>
/// Hotkey modifier flags. Values intentionally equal RegisterHotKey's MOD_*
/// constants (MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4, MOD_WIN=0x8) so
/// the hotkey manager can cast straight to the registration parameter.
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>
/// Virtual-key subset usable as the key part of a global hotkey. Member values
/// are the literal Windows VK codes (identical to the WinForms <c>Keys</c>
/// enum), so the capture box casts <c>KeyEventArgs.KeyCode</c> directly and
/// the hotkey manager passes <c>(uint)(int)Key</c> to RegisterHotKey.
/// Modifier keys themselves (VK 0x10-0x12, 0x5B/0x5C, 0xA0-0xA6), Tab, and
/// layout-dependent OEM keys are deliberately absent: bare-modifier presses
/// stay ignored during capture, and unsupported keys fail parsing loudly
/// instead of silently mapping to a wrong VK.
/// </summary>
public enum HotkeyKey : int
{
    Backspace = 0x08,
    Enter = 0x0D,
    Pause = 0x13,
    Escape = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Insert = 0x2D,
    Delete = 0x2E,
    D0 = 0x30,
    D1 = 0x31,
    D2 = 0x32,
    D3 = 0x33,
    D4 = 0x34,
    D5 = 0x35,
    D6 = 0x36,
    D7 = 0x37,
    D8 = 0x38,
    D9 = 0x39,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    NumPad0 = 0x60,
    NumPad1 = 0x61,
    NumPad2 = 0x62,
    NumPad3 = 0x63,
    NumPad4 = 0x64,
    NumPad5 = 0x65,
    NumPad6 = 0x66,
    NumPad7 = 0x67,
    NumPad8 = 0x68,
    NumPad9 = 0x69,
    Multiply = 0x6A,
    Add = 0x6B,
    Subtract = 0x6D,
    Decimal = 0x6E,
    Divide = 0x6F,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
}

/// <summary>A parsed hotkey: modifier flags plus the virtual key.</summary>
public sealed record HotkeyCombination(HotkeyModifiers Modifiers, HotkeyKey Key);
