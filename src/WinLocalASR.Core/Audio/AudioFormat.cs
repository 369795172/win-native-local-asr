namespace WinLocalASR.Core.Audio;

public enum SampleFormat
{
    Pcm16,
    Float32,
}

public sealed record AudioFormat(int SampleRate, int Channels, SampleFormat Format)
{
    public int BitsPerSample => Format == SampleFormat.Pcm16 ? 16 : 32;

    public int BlockAlign => Channels * BitsPerSample / 8;
}
