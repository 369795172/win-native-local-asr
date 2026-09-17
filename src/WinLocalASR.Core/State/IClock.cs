namespace WinLocalASR.Core.State;

/// <summary>Time seam for ProcessingStartedAt / ProcessingDuration (Swift: Date()).</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
