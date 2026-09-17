namespace WinLocalASR.Core.State;

/// <summary>Swift AppState.Phase payload carrier for user-facing error text.</summary>
public static class StateStrings
{
    /// <summary>Swift LocalizableStrings.notConfigured.</summary>
    public const string NotConfigured = "Not configured";

    /// <summary>Swift LocalizableStrings.venvNotReady.</summary>
    public const string SetupRequired = "ASR not configured. Click Setup to install.";

    /// <summary>Swift LocalizableStrings.engineNotReady.</summary>
    public const string EngineNotReady = "ASR engine is not ready";

    /// <summary>Swift LocalizableStrings.emptyTranscript.</summary>
    public const string EmptyTranscript = "No speech was recognized";

    /// <summary>Swift LocalizableStrings.recordingCancelled.</summary>
    public const string RecordingCancelled = "Recording cancelled";

    /// <summary>Swift LocalizableStrings.copiedToClipboard.</summary>
    public const string CopiedToClipboard = "Copied to clipboard";

    /// <summary>Swift LocalizableStrings.clipboardCopyFailed.</summary>
    public const string ClipboardCopyFailed = "Clipboard copy failed. Retry from the main window.";
}
