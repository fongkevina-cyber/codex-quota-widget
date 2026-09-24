using System.IO;
using CodexQuotaWidget.App.Infrastructure;

namespace CodexQuotaWidget.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    private string _directory = null!;
    private string _settingsPath = null!;
    private string _logPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "CodexQuotaWidgetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
        _logPath = Path.Combine(_directory, "widget.log");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void Load_MigratesSchemaOneWithoutDiscardingFloatingCoordinates()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "schemaVersion": 1,
              "topmost": false,
              "autoStart": false,
              "monitorDeviceName": "\\\\.\\DISPLAY1",
              "leftPixels": 123,
              "topPixels": 456
            }
            """);

        var settings = CreateService().Load();

        Assert.AreEqual(WindowHostMode.Floating, settings.HostMode);
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.IsFalse(settings.Topmost);
        Assert.IsFalse(settings.AutoStart);
        Assert.AreEqual("\\\\.\\DISPLAY1", settings.MonitorDeviceName);
        Assert.AreEqual(123, settings.LeftPixels);
        Assert.AreEqual(456, settings.TopPixels);
        StringAssert.Contains(File.ReadAllText(_settingsPath), "\"schemaVersion\": 2");
    }

    [TestMethod]
    public void SaveAndLoad_PreservesTaskbarHostMode()
    {
        var service = CreateService();
        service.Save(new AppSettings
        {
            HostMode = WindowHostMode.Taskbar,
            Topmost = false,
            LeftPixels = 10,
            TopPixels = 20,
        });

        var reloaded = CreateService().Load();

        Assert.AreEqual(WindowHostMode.Taskbar, reloaded.HostMode);
        Assert.IsFalse(reloaded.Topmost);
        Assert.AreEqual(10, reloaded.LeftPixels);
        Assert.AreEqual(20, reloaded.TopPixels);
    }

    [TestMethod]
    public void Load_CoercesUnknownHostModeToFloating()
    {
        File.WriteAllText(
            _settingsPath,
            """
            {
              "schemaVersion": 2,
              "hostMode": 7,
              "leftPixels": 5
            }
            """);

        var settings = CreateService().Load();

        Assert.AreEqual(WindowHostMode.Floating, settings.HostMode);
        Assert.AreEqual(5, settings.LeftPixels);
    }

    [TestMethod]
    public void Load_WithoutFile_UsesFloatingDefault()
    {
        var settings = CreateService().Load();

        Assert.AreEqual(WindowHostMode.Floating, settings.HostMode);
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    private SettingsService CreateService() => new(new AppLogger(_logPath), _settingsPath);
}
