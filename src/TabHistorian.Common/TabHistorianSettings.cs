using System.Text.Json;

namespace TabHistorian.Common;

public class TabHistorianSettings
{
    public string DatabasePath { get; set; } = "tabhistorian.db";
    public string BackupDirectory { get; set; } = "backups";

    public required string SettingsDirectory { get; init; }
    public required string ResolvedDatabasePath { get; init; }
    public required string ResolvedBackupDirectory { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static TabHistorianSettings Load()
    {
        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TabHistorian");
        Directory.CreateDirectory(settingsDir);

        var settingsPath = Path.Combine(settingsDir, "settings.json");

        string databasePath = "tabhistorian.db";
        string backupDirectory = "backups";

        if (File.Exists(settingsPath))
        {
            var json = File.ReadAllText(settingsPath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            if (doc.TryGetProperty("databasePath", out var dbProp) && dbProp.ValueKind == JsonValueKind.String)
                databasePath = dbProp.GetString()!;
            if (doc.TryGetProperty("backupDirectory", out var backupProp) && backupProp.ValueKind == JsonValueKind.String)
                backupDirectory = backupProp.GetString()!;
        }
        else
        {
            var defaults = new { databasePath, backupDirectory };
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(defaults, JsonOptions));
        }

        return new TabHistorianSettings
        {
            DatabasePath = databasePath,
            BackupDirectory = backupDirectory,
            SettingsDirectory = settingsDir,
            ResolvedDatabasePath = ResolvePath(settingsDir, databasePath),
            ResolvedBackupDirectory = ResolvePath(settingsDir, backupDirectory),
        };
    }

    private static string ResolvePath(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);
}
