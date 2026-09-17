using WinLocalASR.Core.Audio;
using Xunit;

namespace WinLocalASR.Tests.Audio;

public class Pcm16WavWriterTests
{
    [Fact]
    public void MakeWav_WritesCanonicalHeaderFor24kMono()
    {
        byte[] pcm = AudioTestHelpers.SinePcm16(440, 24000, 0.01, amplitude: 0.5);

        var parsed = AudioTestHelpers.ParsePcmWav(Pcm16WavWriter.MakeWav(pcm, sampleRate: 24000, channels: 1));

        Assert.Equal(24000, parsed.SampleRate);
        Assert.Equal(1, parsed.Channels);
        Assert.Equal(16, parsed.BitsPerSample);
        Assert.Equal(pcm, parsed.Data);
    }

    [Fact]
    public void MakeWav_EmptyPcm_ProducesHeaderOnlyFile()
    {
        byte[] wav = Pcm16WavWriter.MakeWav(Array.Empty<byte>(), sampleRate: 24000, channels: 1);

        Assert.Equal(44, wav.Length);
        var parsed = AudioTestHelpers.ParsePcmWav(wav);
        Assert.Empty(parsed.Data);
    }

    [Fact]
    public void WriteWavFile_WritesParseableFileAndReturnsPath()
    {
        byte[] pcm = AudioTestHelpers.SinePcm16(440, 24000, 0.01, amplitude: 0.2);
        string path = Path.Combine(Path.GetTempPath(), $"WinLocalASR-test-{Guid.NewGuid()}.wav");
        try
        {
            string returned = Pcm16WavWriter.WriteWavFile(path, pcm, sampleRate: 24000, channels: 1);
            Assert.Equal(path, returned);
            Assert.Equal(pcm, AudioTestHelpers.ParsePcmWav(File.ReadAllBytes(path)).Data);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
