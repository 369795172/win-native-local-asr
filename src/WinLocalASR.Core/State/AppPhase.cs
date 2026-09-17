namespace WinLocalASR.Core.State;

/// <summary>
/// Swift AppState.Phase — a payload-carrying enum ported as a sealed record hierarchy so
/// Error(string) keeps its message inside the value (structural equality, like Swift's
/// Equatable enum with an associated value). The empty cases are exposed as cached
/// singleton properties.
/// </summary>
public abstract record AppPhase
{
    private AppPhase() { }

    public sealed record LoadingPhase : AppPhase;
    public sealed record IdlePhase : AppPhase;
    public sealed record RecordingPhase : AppPhase;
    public sealed record ProcessingPhase : AppPhase;
    public sealed record ErrorPhase(string Message) : AppPhase;

    public static readonly AppPhase Loading = new LoadingPhase();
    public static readonly AppPhase Idle = new IdlePhase();
    public static readonly AppPhase Recording = new RecordingPhase();
    public static readonly AppPhase Processing = new ProcessingPhase();

    public static AppPhase Error(string message) => new ErrorPhase(message);
}
