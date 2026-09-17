namespace WinLocalASR.Core.Shell;

/// <summary>Real named <see cref="System.Threading.Mutex"/> factory (BCL, cross-platform).</summary>
public sealed class SystemMutexFactory : IMutexFactory
{
    public IMutex Create(string name) => new SystemMutex(name);

    private sealed class SystemMutex : IMutex
    {
        private readonly Mutex _mutex;

        public SystemMutex(string name) => _mutex = new Mutex(false, name);

        public bool TryAcquire() => _mutex.WaitOne(0);

        public void Release() => _mutex.ReleaseMutex();

        public void Dispose() => _mutex.Dispose();
    }
}
