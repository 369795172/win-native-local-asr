using System.Buffers.Binary;

namespace WinLocalASR.Core.Audio;

/// <summary>
/// Canonical RIFF/WAVE writer for PCM16 data (port of PCM16WAVEncoder.makeWAV):
/// 44-byte header, PCM(1) fmt chunk of 16 bytes, then the data chunk.
/// This WAV is the interchange format consumed by llama-server /v1/audio/transcriptions.
/// </summary>
public static class Pcm16WavWriter
{
    public const int HeaderLength = 44;

    public static byte[] MakeWav(ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        var wav = new byte[HeaderLength + pcm.Length];

        wav[0] = (byte)'R';
        wav[1] = (byte)'I';
        wav[2] = (byte)'F';
        wav[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + pcm.Length);
        wav[8] = (byte)'W';
        wav[9] = (byte)'A';
        wav[10] = (byte)'V';
        wav[11] = (byte)'E';

        wav[12] = (byte)'f';
        wav[13] = (byte)'m';
        wav[14] = (byte)'t';
        wav[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16); // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * channels * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)(channels * 2)); // block align
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16); // bits per sample

        wav[36] = (byte)'d';
        wav[37] = (byte)'a';
        wav[38] = (byte)'t';
        wav[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length);

        pcm.CopyTo(wav.AsSpan(HeaderLength));
        return wav;
    }

    /// <summary>Writes the WAV file and returns <paramref name="path"/> (Swift stopRecording -> URL).</summary>
    public static string WriteWavFile(string path, ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        File.WriteAllBytes(path, MakeWav(pcm, sampleRate, channels));
        return path;
    }
}
