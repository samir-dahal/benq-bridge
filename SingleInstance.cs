namespace BenqBridge;

/// <summary>
/// One running copy per user session and port. A second launch does not start a second server:
/// it opens the pairing page, or with --stop asks the running copy to quit.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private EventWaitHandle? _stopEvent;
    private RegisteredWaitHandle? _stopRegistration;

    private SingleInstance(Mutex mutex, bool isFirst)
    {
        _mutex = mutex;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire(int port)
    {
        // Existence of the named mutex is the signal. It is never "owned", so there is no
        // thread-affine release to get wrong across awaits.
        var mutex = new Mutex(false, $@"Local\BenqBridge-{port}", out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>Asks the running copy on this port to shut down. Does nothing if none is running.</summary>
    public static void RequestStop(int port)
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName(port), out var handle)) return;

        using (handle) handle.Set();
    }

    /// <summary>Runs onStop (on a thread-pool thread) when another launch calls RequestStop.</summary>
    public void ListenForStop(int port, Action onStop)
    {
        _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(port));
        _stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            _stopEvent, (_, _) => onStop(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public void Dispose()
    {
        _stopRegistration?.Unregister(null);
        _stopEvent?.Dispose();
        _mutex.Dispose();
    }

    private static string StopEventName(int port) => $@"Local\BenqBridge-{port}-stop";
}
