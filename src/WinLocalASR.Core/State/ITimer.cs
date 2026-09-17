namespace WinLocalASR.Core.State;

/// <summary>
/// One-shot scheduling seam. Swift's state machine uses Task.sleep one-shots plus one
/// repeating Timer; here everything is a cancellable one-shot (Dispose cancels) and the
/// repeating recording timer re-arms itself, mirroring Swift's explicit
/// invalidate-then-reschedule patterns. Callbacks may run on any thread — the controller
/// marshals them through <see cref="IDispatcher"/> — and implementations must tolerate
/// Schedule calls made from inside callbacks.
/// </summary>
public interface ITimer
{
    /// <summary>Schedule <paramref name="callback"/> after <paramref name="delay"/>.</summary>
    /// <returns>Handle whose Dispose cancels the pending callback.</returns>
    IDisposable Schedule(TimeSpan delay, Action callback);
}
