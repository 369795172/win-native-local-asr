namespace WinLocalASR.Core.State;

/// <summary>Swift AppError.emptyTranscript — thrown when the trimmed transcript is empty.</summary>
public sealed class EmptyTranscriptException : Exception
{
    public EmptyTranscriptException()
        : base(StateStrings.EmptyTranscript)
    {
    }
}
