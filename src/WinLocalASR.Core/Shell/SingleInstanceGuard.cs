namespace WinLocalASR.Core.Shell;

/// <summary>Named-mutex abstraction so single-instance logic is unit-testable off-Windows.</summary>
public interface IMutex : IDisposable
{
    /// <summary>Attempts immediate ownership; never blocks (zero-timeout semantics).</summary>
    bool TryAcquire();

    void Release();
}

public interface IMutexFactory
{
    IMutex Create(string name);
}

/// <summary>
/// Cross-process single-instance gate over <c>Global\WinLocalASR</c>. The second launch
/// must observe <see cref="IsPrimary"/> == false and exit silently with code 0 — never an
/// error dialog, never a second tray. An <see cref="AbandonedMutexException"/> means the
/// previous holder died without releasing, so this instance claims ownership.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Global\WinLocalASR";

    private readonly IMutex _mutex;

    public SingleInstanceGuard(IMutexFactory? factory = null, string name = DefaultMutexName)
    {
        _mutex = (factory ?? new SystemMutexFactory()).Create(name);
        bool acquired;
        try
        {
            acquired = _mutex.TryAcquire();
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        IsPrimary = acquired;
    }

    public bool IsPrimary { get; }

    public void Dispose()
    {
        if (IsPrimary)
        {
            try
            {
                _mutex.Release();
            }
            catch (ApplicationException)
            {
                // Already released by this thread — nothing to do.
            }
        }

        _mutex.Dispose();
    }
}
