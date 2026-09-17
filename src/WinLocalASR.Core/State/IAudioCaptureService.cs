namespace WinLocalASR.Core.State;

/// <summary>
/// Narrow slice of <c>Audio.AudioCaptureManager</c> consumed by the state machine (Swift
/// AppState drives AudioCaptureManager directly; the C# port keeps this seam so tests
/// inject fakes without touching the real WASAPI pipeline). Callback properties mirror the
/// manager's settable-Action semantics.
/// </summary>
public interface IAudioCaptureService
{
    string? SelectedDeviceId { get; set; }

    double MaximumDuration { get; set; }

    /// <summary>Raised when the recording limit elapses; stopping is the controller's job.</summary>
    Action? OnMaximumDuration { get; set; }

    /// <summary>Raised per PCM chunk with the normalized level in [0, 1].</summary>
    Action<float>? OnAudioLevel { get; set; }

    void StartRecording(string? deviceId = null);

    /// <summary>Finalize the WAV file and return its path.</summary>
    string StopRecording();

    /// <summary>Discard the active recording without producing a WAV file.</summary>
    void CancelRecording();
}
