using WinLocalASR.Core.Inference;
using Xunit;

namespace WinLocalASR.Tests.Inference;

public class TranscriptionTimeoutTests
{
    [Theory]
    [InlineData(0, 90)]      // floor dominates short audio
    [InlineData(1, 90)]      // 3*1+20 = 23 < 90
    [InlineData(30, 110)]    // 3*30+20 = 110
    [InlineData(60, 200)]    // 3*60+20 = 200
    [InlineData(120, 380)]   // 3*120+20 = 380 (the 120 s recording cap)
    public void Compute_follows_max_90_3d_plus_20(double durationSeconds, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TranscriptionTimeout.Compute(TimeSpan.FromSeconds(durationSeconds)));
    }

    [Theory]
    [InlineData(1)]     // 48000 bytes at 24 kHz mono 16-bit
    [InlineData(60)]    // 2880000 bytes
    [InlineData(120)]   // 5760000 bytes
    public void Wav_duration_parser_reads_24khz_mono_pcm16(double seconds)
    {
        (string path, byte[] _) = InferenceWavFile.WriteTempWav(seconds);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), WavDurationParser.ParseDuration(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Timeout_from_parsed_wav_duration_end_to_end()
    {
        (string path, byte[] _) = InferenceWavFile.WriteTempWav(60);
        try
        {
            TimeSpan timeout = TranscriptionTimeout.Compute(WavDurationParser.ParseDuration(path));
            Assert.Equal(TimeSpan.FromSeconds(200), timeout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Wav_parser_rejects_non_wav_bytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winlocalasr-test-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "not a wav file at all");
        try
        {
            Assert.Throws<InvalidDataException>(() => WavDurationParser.ParseDuration(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
