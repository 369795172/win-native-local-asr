namespace WinLocalASR.Core.State;

/// <summary>
/// <see cref="ITimer"/> over System.Threading.Timer. Callbacks run on threadpool threads —
/// the controller marshals them through <see cref="IDispatcher"/>, so this implementation
/// carries no thread affinity.
/// </summary>
public sealed class SystemSchedulingTimer : ITimer
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        return new System.Threading.Timer(
            _ => callback(),
            null,
            delay,
            System.Threading.Timeout.InfiniteTimeSpan);
    }
}
