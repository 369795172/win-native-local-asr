using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinLocalASR.Core.Audio;

/// <summary>
/// WASAPI-backed device factory. Only its methods touch Windows APIs, and they run
/// exclusively on Windows execution paths (CI windows-latest or a real machine).
/// </summary>
public sealed class WindowsAudioDeviceFactory : IAudioDeviceFactory
{
    public IReadOnlyList<AudioDeviceInfo> ListInputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
                }
            }
        }
        return devices;
    }

    public IAudioCaptureSource? OpenCapture(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        MMDevice? device = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                device = enumerator.GetDevice(deviceId);
            }
            catch
            {
                device = null; // unknown/unplugged id falls back to the default (Swift: AVCaptureDevice(uniqueID:) -> nil)
            }
        }

        if (device is null)
        {
            try
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            catch
            {
                return null; // no capture endpoint at all (CI-runner shape) -> Swift noInputDevice
            }
        }

        try
        {
            return new WasapiCaptureSource(device);
        }
        catch
        {
            device.Dispose();
            return null;
        }
    }
}

/// <summary>WasapiCapture (shared mode) adapter; owns the MMDevice lifetime.</summary>
internal sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private bool _disposed;

    internal WasapiCaptureSource(MMDevice device)
    {
        _device = device;
        _capture = new WasapiCapture(device); // shared-mode capture of the selected device
        WaveFormat format = _capture.WaveFormat;
        Format = format.Encoding == WaveFormatEncoding.IeeeFloat
            ? new AudioFormat(format.SampleRate, format.Channels, SampleFormat.Float32)
            : new AudioFormat(format.SampleRate, format.Channels, SampleFormat.Pcm16);
        _capture.DataAvailable += (_, e) =>
            PcmChunkReceived?.Invoke(this, new ReadOnlyMemory<byte>(e.Buffer, 0, e.BytesRecorded));
    }

    public AudioFormat Format { get; }

    public event EventHandler<ReadOnlyMemory<byte>>? PcmChunkReceived;

    public void Start() => _capture.StartRecording();

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
        }
        _capture.Dispose(); // joins the capture thread
        _device.Dispose();
    }
}
