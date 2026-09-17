namespace WinLocalASR.Core.State;

/// <summary>
/// <see cref="IDispatcher"/> over a captured <see cref="SynchronizationContext"/>
/// (construct it on the UI thread). Falls back to inline execution when no context exists,
/// which matches headless runs and unit tests.
/// </summary>
public sealed class SynchronizationContextDispatcher : IDispatcher
{
    private readonly SynchronizationContext? _context;

    public SynchronizationContextDispatcher(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current;
    }

    public void Post(Action action)
    {
        if (_context is null)
        {
            action();
            return;
        }

        _context.Post(_ => action(), null);
    }
}
