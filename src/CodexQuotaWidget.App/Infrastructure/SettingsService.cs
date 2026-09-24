using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.App.Infrastructure;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool Topmost { get; set; } = true;

    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Whether the capsule floats on the desktop or is attached inside the taskbar.
    /// The default stays floating until the taskbar host has been validated on the device.
    /// </summary>
    public WindowHostMode HostMode { get; set; } = WindowHostMode.Floating;

    public string? MonitorDeviceName { get; set; }

    public int? LeftPixels { get; set; }

    public int? TopPixels { get; set; }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly AppLogger _logger;
    private readonly string _settingsPath;
    private readonly bool _usesDefaultLocation;

    public SettingsService(AppLogger logger)
        : this(logger, Path.Combine(AppDataOwnership.DataDirectory, "settings.json"), true)
    {
    }

    internal SettingsService(AppLogger logger, string settingsPath)
        : this(logger, settingsPath, false)
    {
    }

    private SettingsService(AppLogger logger, string settingsPath, bool usesDefaultLocation)
    {
        _logger = logger;
        _settingsPath = settingsPath;
        _usesDefaultLocation = usesDefaultLocation;
    }

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                EnsureStorageDirectory();
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                    Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
                }

                if (Migrate(Current))
                {
                    Persist(Current);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                _logger.Write("settings_load_failed", ex);
                Current = new AppSettings();
            }

            return Current;
        }
    }

    public void SaveCurrent() => Save(Current);

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                EnsureStorageDirectory();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.Write("settings_save_failed", ex);
                return;
            }

            Persist(settings);
        }
    }

    /// <summary>
    /// Brings older or partially written settings forward without discarding saved
    /// floating coordinates. Returns true when anything was normalized.
    /// </summary>
    internal static bool Migrate(AppSettings settings)
    {
        var changed = false;

        if (!Enum.IsDefined(settings.HostMode))
        {
            settings.HostMode = WindowHostMode.Floating;
            changed = true;
        }

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            changed = true;
        }

        return changed;
    }

    private void EnsureStorageDirectory()
    {
        if (_usesDefaultLocation)
        {
            AppDataOwnership.EnsureDataDirectory();
            return;
        }

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void Persist(AppSettings settings)
    {
        try
        {
            var temporaryPath = _settingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
            Current = settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Write("settings_save_failed", ex);
        }
    }
}
