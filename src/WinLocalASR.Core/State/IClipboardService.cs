namespace WinLocalASR.Core.State;

/// <summary>Swift TextOutputManager.copy — false means the clipboard write failed.</summary>
public interface IClipboardService
{
    bool SetText(string text);
}
