namespace CodexQuotaWidget.App.Infrastructure;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Local\\CodexQuotaWidget.Singleton.v1";
    private const string ActivationEventName = "Local\\CodexQuotaWidget.Activate.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _ownsMutex;

    public event EventHandler? ActivationRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalRunningInstance();
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _ownsMutex = true;
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName,
            out _);
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
        return true;
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first process may still be between acquiring the mutex and creating the event.
        }
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _registeredWait = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already shutting down; disposing the handle is sufficient.
            }
        }

        _mutex?.Dispose();
        _mutex = null;
        _ownsMutex = false;
    }
}
