using System.Threading;

namespace MyPowerTools.Platform.Abstractions;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;

    private SingleInstanceGuard(Mutex? mutex, bool ownsInstance)
    {
        _mutex = mutex;
        OwnsInstance = ownsInstance;
    }

    public bool OwnsInstance { get; }

    public static SingleInstanceGuard Acquire(string name)
    {
        var mutexName = OperatingSystem.IsWindows() ? $@"Global\{name}" : name;
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        return new SingleInstanceGuard(mutex, createdNew);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (OwnsInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Async continuations can dispose on a different thread than the owning mutex thread.
            }
        }

        _mutex.Dispose();
    }
}
