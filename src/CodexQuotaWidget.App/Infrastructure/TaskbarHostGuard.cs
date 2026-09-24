namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Tracks generation, visibility intent and disposal for one taskbar capsule host. Async
/// callbacks capture the generation and are rejected once the host is hidden-generation
/// changed or destroyed, so a stale redraw can never operate on a dead handle.
/// </summary>
internal sealed class TaskbarHostGuard
{
    private int _generation;
    private bool _disposed;
    private bool _hidden;

    public int Generation => _generation;

    public bool IsDisposed => _disposed;

    public bool IsAlive => !_disposed;

    public bool IsHidden => _hidden;

    /// <summary>Starts a fresh, visible generation and returns it.</summary>
    public int StartGeneration()
    {
        _hidden = false;
        return ++_generation;
    }

    public bool IsCurrent(int generation) => !_disposed && generation == _generation;

    public void SetHidden(bool hidden) => _hidden = hidden;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        unchecked
        {
            _generation++;
        }
    }
}
