using Microsoft.Data.Sqlite;
using TabHistorian.Common;

namespace TabHistorian.Services;

public class TabMachineDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TabHistorianSettings _settings;
    private readonly ILogger<TabMachineDb> _logger;

    public TabMachineDb(TabHistorianSettings settings, ILogger<TabMachineDb> logger)
    {
        _settings = settings;
        _logger = logger;
        var dbPath = settings.ResolvedTabMachineDatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connection = new SqliteConnection($"Data Source={dbPath};Default Timeout=30");
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=30000";
            cmd.ExecuteNonQuery();
        }

        InitializeSchema();
        RunMigrations();
        logger.LogInformation("TabMachine database ready at {Path}", dbPath);
    }

    internal SqliteConnection Connection => _connection;

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tab_identities (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_name TEXT NOT NULL,
                first_url TEXT NOT NULL,
                first_title TEXT NOT NULL DEFAULT '',
                first_seen TEXT NOT NULL,
                last_url TEXT NOT NULL,
                last_title TEXT NOT NULL DEFAULT '',
                last_seen TEXT NOT NULL,
                last_active_time TEXT
            );

            CREATE TABLE IF NOT EXISTS tab_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tab_identity_id INTEGER NOT NULL REFERENCES tab_identities(id),
                event_type TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                state_delta TEXT,
                url TEXT,
                title TEXT,
                profile_name TEXT
            );

            CREATE TABLE IF NOT EXISTS tab_current_state (
                tab_identity_id INTEGER PRIMARY KEY REFERENCES tab_identities(id),
                current_url TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                pinned INTEGER DEFAULT 0,
                last_active_time TEXT,
                tab_index INTEGER NOT NULL DEFAULT 0,
                window_index INTEGER NOT NULL DEFAULT 0,
                window_type INTEGER DEFAULT 0,
                profile_name TEXT NOT NULL,
                profile_display_name TEXT,
                sync_tab_node_id TEXT,
                tab_group_token TEXT,
                extension_app_id TEXT,
                navigation_history TEXT,
                show_state INTEGER DEFAULT 0,
                is_active INTEGER DEFAULT 0,
                is_open INTEGER DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_tm_identities_profile ON tab_identities(profile_name);
            CREATE INDEX IF NOT EXISTS idx_tm_events_identity ON tab_events(tab_identity_id);
            CREATE INDEX IF NOT EXISTS idx_tm_events_type ON tab_events(event_type);
            CREATE INDEX IF NOT EXISTS idx_tm_events_timestamp ON tab_events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_tm_events_url ON tab_events(url);
            CREATE INDEX IF NOT EXISTS idx_tm_current_state_open ON tab_current_state(is_open);
            CREATE INDEX IF NOT EXISTS idx_tm_current_state_sync ON tab_current_state(sync_tab_node_id);
            """;
        cmd.ExecuteNonQuery();
    }

    private void RunMigrations()
    {
        // Migration 1: Add first_active_time and last_navigated to tab_identities
        if (!ColumnExists("tab_identities", "first_active_time"))
        {
            ExecuteNonQuery("ALTER TABLE tab_identities ADD COLUMN first_active_time TEXT");
            ExecuteNonQuery("ALTER TABLE tab_identities ADD COLUMN last_navigated TEXT");
            ExecuteNonQuery("""
                UPDATE tab_identities SET first_active_time = (
                    SELECT cs.last_active_time FROM tab_current_state cs
                    WHERE cs.tab_identity_id = tab_identities.id
                )
                """);
            ExecuteNonQuery("UPDATE tab_identities SET last_navigated = first_seen");
        }

        // Migration 2: Fix last_navigated backfill — should be null unless tab actually navigated
        if (!ColumnExists("tab_identities", "_m2_nav_fix"))
        {
            ExecuteNonQuery("ALTER TABLE tab_identities ADD COLUMN _m2_nav_fix INTEGER DEFAULT 1");
            ExecuteNonQuery("""
                UPDATE tab_identities SET last_navigated = (
                    SELECT MAX(te.timestamp) FROM tab_events te
                    WHERE te.tab_identity_id = tab_identities.id
                      AND te.event_type = 'Navigated'
                )
                """);
        }
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
                return true;
        }
        return false;
    }

    public void BackupDatabase()
    {
        var backupDir = _settings.ResolvedBackupDirectory;
        var backupName = $"tabmachine-{DateTime.UtcNow:yyyy-MM-dd}.db";
        var backupPath = Path.Combine(backupDir, backupName);

        if (File.Exists(backupPath))
        {
            _logger.LogDebug("TabMachine backup already exists for today: {Path}", backupPath);
            return;
        }

        var dbPath = _settings.ResolvedTabMachineDatabasePath;
        var dbSize = new FileInfo(dbPath).Length;
        _logger.LogInformation("Starting TabMachine backup ({DbSize:F1} MB) to {Path}",
            dbSize / (1024.0 * 1024.0), backupPath);

        Directory.CreateDirectory(backupDir);

        try
        {
            using var destConn = new SqliteConnection($"Data Source={backupPath}");
            destConn.Open();
            _connection.BackupDatabase(destConn, "main", "main");
            destConn.Close();

            var backupSize = new FileInfo(backupPath).Length;
            _logger.LogInformation("TabMachine backup complete ({BackupSize:F1} MB): {Path}",
                backupSize / (1024.0 * 1024.0), backupPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TabMachine backup failed, cleaning up partial file: {Path}", backupPath);
            try { File.Delete(backupPath); }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean up partial backup file: {Path}", backupPath);
            }
            throw;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
