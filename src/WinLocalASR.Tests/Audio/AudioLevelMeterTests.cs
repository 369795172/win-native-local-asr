using WinLocalASR.Core.Audio;
using Xunit;

namespace WinLocalASR.Tests.Audio;

public class AudioLevelMeterTests
{
    [Fact]
    public void Silence_ClampsToZero()
    {
        short[] silence = new short[2400];
        Assert.Equal(0f, AudioLevelMeter.ComputeLevel(silence));
    }

    [Fact]
    public void EmptyBuffer_ClampsToZero()
    {
        Assert.Equal(0f, AudioLevelMeter.ComputeLevel(Array.Empty<short>()));
    }

    [Fact]
    public void LoudSine_ClampsToOne()
    {
        byte[] loud = AudioTestHelpers.SinePcm16(440, 24000, 0.05, amplitude: 0.5);
        Assert.Equal(1f, AudioLevelMeter.ComputeLevel(loud));
    }

    [Fact]
    public void QuarterScaleSquare_RmsExactlyQuarter_ReportsFormulaValue()
    {
        // Alternating +/-8192 has RMS exactly 0.25 -> dB = -12.0412 -> level = 67.96/70 = 0.9709
        var samples = new short[2400];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(i % 2 == 0 ? 8192 : -8192);
        }
        Assert.InRange(AudioLevelMeter.ComputeLevel(samples), 0.965f, 0.975f);
    }

    [Fact]
    public void QuietSine_ReportsLinearMidScale()
    {
        // amplitude 0.001 -> RMS 0.000707 -> -63.0 dB -> (80-63)/70 = 0.243
        byte[] quiet = AudioTestHelpers.SinePcm16(440, 24000, 0.05, amplitude: 0.001);
        Assert.InRange(AudioLevelMeter.ComputeLevel(quiet), 0.22f, 0.26f);
    }
}
