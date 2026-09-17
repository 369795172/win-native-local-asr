namespace WinLocalASR.App;

/// <summary>
/// Message-only WinForms window (HWND_MESSAGE parent): invisible, absent from the
/// window list, and the WM_HOTKEY recipient for every hotkey registered against
/// its handle. The WndProc hands the hotkey id to
/// <see cref="WinLocalASR.Core.Hotkeys.HotkeyManager.HandleWmHotkey"/> — it never
/// calls the controller from the message-loop thread (the dispatcher seam owns
/// marshalling, the Task 7 discipline).
/// </summary>
internal sealed class HotkeyMessageWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int MessageOnlyParent = -1; // HWND_MESSAGE

    /// <summary>Set by the hotkey shell after the manager exists (id → manager).</summary>
    public Action<int>? HotkeyReceived { get; set; }

    public HotkeyMessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "WinLocalASRHotkeyWindow",
            Parent = (IntPtr)MessageOnlyParent,
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            HotkeyReceived?.Invoke(m.WParam.ToInt32());
        }

        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
