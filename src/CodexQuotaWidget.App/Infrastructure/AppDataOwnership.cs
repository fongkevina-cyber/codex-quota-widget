using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.App.Infrastructure;

internal static class AppDataOwnership
{
    public const string MarkerFileName = ".codex-quota-widget-owner.json";
    public const string ApplicationId = "7d19329b-95b1-4e48-a71e-92370683a708";
    public const string ApplicationName = "CodexQuotaWidget";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationName);

    public static string EnsureDataDirectory()
    {
        var directory = DataDirectory;
        var markerPath = Path.Combine(directory, MarkerFileName);
        if (Directory.Exists(directory))
        {
            if (File.Exists(markerPath))
            {
                ValidateMarker(markerPath);
                return directory;
            }

            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new InvalidOperationException(
                    $"Refusing to use an unowned application data directory: '{directory}'.");
            }
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var marker = new OwnershipMarker(1, ApplicationId, ApplicationName);
        var temporaryPath = markerPath + ".tmp";
        var json = JsonSerializer.Serialize(
            marker,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });
        File.WriteAllText(
            temporaryPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, markerPath, overwrite: false);
        ValidateMarker(markerPath);
        return directory;
    }

    private static void ValidateMarker(string markerPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath, Encoding.UTF8));
            var root = document.RootElement;
            var valid = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out var schemaVersion)
                && schemaVersion.TryGetInt32(out var parsedVersion)
                && parsedVersion == 1
                && root.TryGetProperty("applicationId", out var applicationId)
                && string.Equals(applicationId.GetString(), ApplicationId, StringComparison.Ordinal)
                && root.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), ApplicationName, StringComparison.Ordinal);
            if (!valid)
            {
                throw new InvalidOperationException(
                    $"Application data ownership marker is invalid: '{markerPath}'.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Application data ownership marker is invalid: '{markerPath}'.",
                exception);
        }
    }

    private sealed record OwnershipMarker(
        int SchemaVersion,
        string ApplicationId,
        string Name);
}
