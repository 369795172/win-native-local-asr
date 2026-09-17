namespace WinLocalASR.Core.Audio;

/// <summary>
/// Abstraction over audio device enumeration/capture so tests never need real hardware.
/// Mirrors the AVFoundation device lookup in AudioCaptureManager.swift.
/// </summary>
public interface IAudioDeviceFactory
{
    IReadOnlyList<AudioDeviceInfo> ListInputDevices();

    /// <summary>
    /// Opens a capture source for <paramref name="deviceId"/>, or the system default
    /// capture device when null/empty. Returns null when no input device exists
    /// (Swift: AudioCaptureError.noInputDevice).
    /// </summary>
    IAudioCaptureSource? OpenCapture(string? deviceId);
}
