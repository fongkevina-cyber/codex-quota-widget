using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Returns initialization and refresh pages to Windows after the UI becomes idle.
/// The pages remain reloadable; no application state or quota data is discarded.
/// </summary>
internal sealed class IdleMemoryTrimmer : IDisposable
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _timer;
    private readonly AppLogger _logger;
    private readonly Func<bool> _trim;
    private bool _failureLogged;
    private bool _disposed;

    public IdleMemoryTrimmer(Dispatcher dispatcher, AppLogger logger)
        : this(dispatcher, logger, DefaultDelay, TryEmptyWorkingSet)
    {
    }

    internal IdleMemoryTrimmer(
        Dispatcher dispatcher,
        AppLogger logger,
        TimeSpan delay,
        Func<bool> trim)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(trim);
        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        _logger = logger;
        _trim = trim;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = delay,
        };
        _timer.Tick += OnTimerTick;
    }

    public void Schedule()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    internal bool TrimNow()
    {
        if (_disposed)
        {
            return false;
        }

        _timer.Stop();
        return ExecuteTrim();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        ExecuteTrim();
    }

    private bool ExecuteTrim()
    {
        try
        {
            var succeeded = _trim();
            if (!succeeded && !_failureLogged)
            {
                _failureLogged = true;
                _logger.Write(
                    "working_set_trim_failed",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            return succeeded;
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                _logger.Write("working_set_trim_failed", ex);
            }

            return false;
        }
    }

    private static bool TryEmptyWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        return EmptyWorkingSet(process.Handle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint processHandle);
}
