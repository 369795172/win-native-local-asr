using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace WinLocalASR.Core.Hotkeys;

/// <summary>
/// Real <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> P/Invoke against one window
/// handle. Lives in Core (not App) so the Windows-only SkippableFacts exercise the
/// exact wrapper the app ships; off-Windows construction/invocation is guarded by
/// the tests' platform skip, mirroring the RegistryKeyFactory precedent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    private readonly IntPtr _hwnd;

    /// <param name="hwnd">Recipient window for WM_HOTKEY. <see cref="IntPtr.Zero"/>
    /// binds to the calling thread's message queue (test usage without a window).</param>
    public Win32HotkeyRegistrar(IntPtr hwnd) => _hwnd = hwnd;

    public bool Register(int id, uint modifiers, uint virtualKey) =>
        RegisterHotKey(_hwnd, id, modifiers, virtualKey);

    public bool Unregister(int id) => UnregisterHotKey(_hwnd, id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
