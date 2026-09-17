using System.Runtime.InteropServices;

namespace WinLocalASR.Core.Audio;

/// <summary>
/// RMS level for 16-bit mono PCM, porting the formula in AudioCaptureManager.swift
/// lines 219-227: rms over samples/32768, dB = 20*log10(max(rms, 1e-7)),
/// level = clamp((dB + 80) / 70, 0, 1).
/// </summary>
public static class AudioLevelMeter
{
    public static float ComputeLevel(ReadOnlySpan<byte> pcm16Bytes)
        => ComputeLevel(MemoryMarshal.Cast<byte, short>(pcm16Bytes));

    /// <summary>Same formula over native float32 capture buffers (WasapiCapture shared
    /// mode is IEEE float); keeps the live meter fed from the input side even when the
    /// resampler batches its output until stop (MediaFoundationPcmResampler).</summary>
    public static float ComputeLevelFloat(ReadOnlySpan<byte> float32Bytes)
        => ComputeLevelFloat(MemoryMarshal.Cast<byte, float>(float32Bytes));

    public static float ComputeLevelFloat(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        double sumSquares = 0;
        foreach (float sample in samples)
        {
            double value = sample;
            sumSquares += value * value;
        }

        double rms = Math.Sqrt(sumSquares / Math.Max(samples.Length, 1));
        double decibels = 20 * Math.Log10(Math.Max(rms, 1e-7));
        return Math.Clamp((float)((decibels + 80) / 70), 0f, 1f);
    }

    public static float ComputeLevel(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        double sumSquares = 0;
        foreach (short sample in samples)
        {
            double value = sample / 32768.0;
            sumSquares += value * value;
        }

        double rms = Math.Sqrt(sumSquares / Math.Max(samples.Length, 1));
        double decibels = 20 * Math.Log10(Math.Max(rms, 1e-7));
        return Math.Clamp((float)((decibels + 80) / 70), 0f, 1f);
    }
}
