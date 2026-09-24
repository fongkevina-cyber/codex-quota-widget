using System.Globalization;
using System.IO;
using System.Text;

namespace CodexQuotaWidget.App.Infrastructure;

/// <summary>
/// Writes intentionally sparse diagnostics. Exception messages and stack traces are omitted
/// so paths, account details, and protocol payloads cannot leak into the widget log.
/// </summary>
public sealed class AppLogger
{
    private const long MaximumLogBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly bool _usesDefaultLocation;

    public AppLogger()
        : this(Path.Combine(AppDataOwnership.DataDirectory, "widget.log"), true)
    {
    }

    internal AppLogger(string logPath)
        : this(logPath, false)
    {
    }

    private AppLogger(string logPath, bool usesDefaultLocation)
    {
        _logPath = logPath;
        _usesDefaultLocation = usesDefaultLocation;
    }

    public void Write(string eventName, Exception? exception = null)
    {
        try
        {
            lock (_gate)
            {
                EnsureStorageDirectory();
                RotateIfNeeded();

                var safeEvent = new string(eventName
                    .Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                    .Take(80)
                    .ToArray());
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:O} event={1}{2}{3}",
                    DateTimeOffset.UtcNow,
                    safeEvent,
                    exception is null ? string.Empty : $" error={exception.GetType().Name}",
                    Environment.NewLine);
                File.AppendAllText(_logPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never keep the widget from running.
        }
    }

    private void EnsureStorageDirectory()
    {
        if (_usesDefaultLocation)
        {
            AppDataOwnership.EnsureDataDirectory();
            return;
        }

        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath) || new FileInfo(_logPath).Length < MaximumLogBytes)
        {
            return;
        }

        var previousPath = _logPath + ".1";
        File.Move(_logPath, previousPath, overwrite: true);
    }
}
