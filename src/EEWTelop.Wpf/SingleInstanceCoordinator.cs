namespace EEWTelop.Wpf;

/// <summary>
/// Keeps one CDI-Telopper process per interactive Windows session and lets a
/// secondary launch ask the running process to restore its control window.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    // Keep these names independent of the application version so stable and
    // beta executables cannot compete for the same OBS routes and settings.
    private const string MutexName = @"Local\QTelopper.SingleInstance";
    private const string ActivationSignalName = @"Local\QTelopper.ActivateRunningInstance";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationSignal;
    private readonly bool _ownsMutex;
    private RegisteredWaitHandle? _activationRegistration;
    private int _disposed;

    public SingleInstanceCoordinator()
    {
        // Create/open the signal before the mutex. If two processes start at
        // nearly the same time, a signal sent by the secondary process remains
        // pending until the primary process registers its wait callback.
        _activationSignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationSignalName,
            out _);
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary CDI-Telopper instance can listen for activation requests.");
        }

        _activationRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _activationSignal,
            (_, timedOut) =>
            {
                if (timedOut || Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                try
                {
                    activationRequested();
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine(exception);
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void NotifyPrimaryInstance()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_ownsMutex)
        {
            _activationSignal.Set();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _activationRegistration?.Unregister(waitObject: null);
        _activationRegistration = null;
        _activationSignal.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
        }

        _mutex.Dispose();
    }
}
