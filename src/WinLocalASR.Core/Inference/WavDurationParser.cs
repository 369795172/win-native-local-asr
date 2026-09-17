using System.Text;

namespace WinLocalASR.Core.Inference;

/// <summary>
/// WAV duration from RIFF chunk walking (no NAudio dependency in the Inference module).
/// Understands canonical PCM WAV as written by Core.Audio.Pcm16WavWriter plus extra chunks.
/// </summary>
public static class WavDurationParser
{
    public static TimeSpan ParseDuration(string wavPath)
    {
        using FileStream stream = File.OpenRead(wavPath);
        return ParseDuration(stream);
    }

    public static TimeSpan ParseDuration(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 12)
        {
            throw new InvalidDataException("Not a RIFF file (too short).");
        }

        ReadExact(reader, 4, "RIFF");
        _ = reader.ReadInt32(); // remaining RIFF size
        ReadExact(reader, 4, "WAVE");

        int byteRate = 0;
        bool sawData = false;
        long dataBytes = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            byte[] chunkId = reader.ReadBytes(4);
            int chunkSize = reader.ReadInt32();
            long chunkStart = stream.Position;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                short audioFormat = reader.ReadInt16();
                _ = reader.ReadInt16(); // channels (byteRate already encodes them)
                int sampleRate = reader.ReadInt32();
                byteRate = reader.ReadInt32();
                _ = reader.ReadInt16(); // block align
                _ = reader.ReadInt16(); // bits per sample
                if (audioFormat != 1)
                {
                    throw new InvalidDataException($"Unsupported WAV audio format {audioFormat} (PCM expected).");
                }

                if (sampleRate <= 0 || byteRate <= 0)
                {
                    throw new InvalidDataException("Invalid WAV fmt chunk (non-positive rate).");
                }
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataBytes = chunkSize;
                sawData = true;
            }

            // chunks are word-aligned
            stream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (byteRate <= 0 || !sawData)
        {
            throw new InvalidDataException("WAV is missing its fmt or data chunk.");
        }

        return TimeSpan.FromSeconds(dataBytes / (double)byteRate);
    }

    private static void ReadExact(BinaryReader reader, int count, string expected)
    {
        byte[] actual = reader.ReadBytes(count);
        if (!actual.SequenceEqual(Encoding.UTF8.GetBytes(expected)))
        {
            throw new InvalidDataException($"Not a WAV file (expected '{expected}' marker).");
        }
    }
}
