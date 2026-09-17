namespace WinLocalASR.Core.Audio;

/// <summary>
/// Error taxonomy mirroring Swift AudioCaptureError one-to-one so the Task 6 state
/// machine port can map cases without reinterpretation.
/// </summary>
public class AudioCaptureException : Exception
{
    public AudioCaptureException(string message) : base(message)
    {
    }
}

/// <summary>Swift: AudioCaptureError.microphoneAccessDenied (Windows privacy setting).</summary>
public sealed class MicrophoneAccessDeniedException : AudioCaptureException
{
    public MicrophoneAccessDeniedException() : base("Microphone access is denied.")
    {
    }
}

/// <summary>Swift: AudioCaptureError.noInputDevice.</summary>
public sealed class NoInputDeviceException : AudioCaptureException
{
    public NoInputDeviceException() : base("No input device is available.")
    {
    }
}

/// <summary>Swift: AudioCaptureError.converterUnavailable.</summary>
public sealed class ConverterUnavailableException : AudioCaptureException
{
    public ConverterUnavailableException() : base("Audio format converter is unavailable.")
    {
    }

    public ConverterUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>Swift: AudioCaptureError.engineStartFailed(String).</summary>
public sealed class EngineStartFailedException : AudioCaptureException
{
    public EngineStartFailedException(string message) : base(message)
    {
    }
}

/// <summary>Swift: AudioCaptureError.notRecording.</summary>
public sealed class NotRecordingException : AudioCaptureException
{
    public NotRecordingException() : base("Recording is not active.")
    {
    }
}

/// <summary>Swift: AudioCaptureError.noAudioCaptured.</summary>
public sealed class NoAudioCapturedException : AudioCaptureException
{
    public NoAudioCapturedException() : base("No audio was captured.")
    {
    }
}
