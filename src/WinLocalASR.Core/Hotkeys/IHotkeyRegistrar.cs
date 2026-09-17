namespace WinLocalASR.Core.Hotkeys;

/// <summary>
/// Win32 <c>RegisterHotKey</c>/<c>UnregisterHotKey</c> seam. The real implementation
/// binds the hotkey ids to a message-only window handle; tests inject fakes and
/// assert the exact (id, modifiers, virtual key) registrations. Thread contract:
/// RegisterHotKey must be called on the thread that owns the window (the UI thread).
/// </summary>
public interface IHotkeyRegistrar
{
    /// <summary>Registers <paramref name="id"/> for the modifier/vk combination.
    /// Returns false when the combination is already held by another program
    /// (ERROR_HOTKEY_ALREADY_REGISTERED) — never throws.</summary>
    bool Register(int id, uint modifiers, uint virtualKey);

    /// <summary>Releases <paramref name="id"/>. Returns true on success; false when
    /// the id was not registered (treated as already-gone by callers).</summary>
    bool Unregister(int id);
}
