using System.Text.Json;
using Microsoft.Data.Sqlite;
using TabHistorian.Models;

namespace TabHistorian.Services;

public class StorageService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<StorageService> _logger;

    public StorageService(ILogger<StorageService> logger, IConfiguration configuration)
    {
        _logger = logger;
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TabHistorian");
        var dbPath = configuration.GetValue<string>("DatabasePath")
            ?? Path.Combine(defaultDir, "tabhistorian.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS windows (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
                profile_name TEXT NOT NULL,
                profile_display_name TEXT,
                window_index INTEGER NOT NULL,
                window_type INTEGER DEFAULT 0,
                x INTEGER, y INTEGER, width INTEGER, height INTEGER,
                show_state INTEGER DEFAULT 0,
                is_active INTEGER DEFAULT 0,
                selected_tab_index INTEGER DEFAULT 0,
                workspace TEXT,
                app_name TEXT,
                user_title TEXT
            );

            CREATE TABLE IF NOT EXISTS tabs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                window_id INTEGER NOT NULL REFERENCES windows(id),
                tab_index INTEGER NOT NULL,
                current_url TEXT NOT NULL,
                title TEXT,
                pinned INTEGER DEFAULT 0,
                last_active_time TEXT,
                tab_group_token TEXT,
                extension_app_id TEXT,
                navigation_history TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_snapshots_timestamp ON snapshots(timestamp);
            CREATE INDEX IF NOT EXISTS idx_tabs_current_url ON tabs(current_url);
            CREATE INDEX IF NOT EXISTS idx_windows_profile ON windows(profile_name);
            CREATE INDEX IF NOT EXISTS idx_tabs_last_active ON tabs(last_active_time);
            """;
        cmd.ExecuteNonQuery();
    }

    public long SaveSnapshot(Snapshot snapshot)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var snapshotCmd = _connection.CreateCommand();
            snapshotCmd.CommandText = "INSERT INTO snapshots (timestamp) VALUES (@ts) RETURNING id";
            snapshotCmd.Parameters.AddWithValue("@ts", snapshot.Timestamp.ToString("O"));
            long snapshotId = (long)snapshotCmd.ExecuteScalar()!;

            foreach (var window in snapshot.Windows)
            {
                using var windowCmd = _connection.CreateCommand();
                windowCmd.CommandText = """
                    INSERT INTO windows (snapshot_id, profile_name, profile_display_name, window_index,
                        window_type, x, y, width, height, show_state, is_active, selected_tab_index,
                        workspace, app_name, user_title)
                    VALUES (@sid, @pn, @pdn, @wi, @wt, @x, @y, @w, @h, @ss, @ia, @sti,
                        @ws, @an, @ut) RETURNING id
                    """;
                windowCmd.Parameters.AddWithValue("@sid", snapshotId);
                windowCmd.Parameters.AddWithValue("@pn", window.ProfileName);
                windowCmd.Parameters.AddWithValue("@pdn", window.ProfileDisplayName);
                windowCmd.Parameters.AddWithValue("@wi", window.WindowIndex);
                windowCmd.Parameters.AddWithValue("@wt", window.WindowType);
                windowCmd.Parameters.AddWithValue("@x", window.X);
                windowCmd.Parameters.AddWithValue("@y", window.Y);
                windowCmd.Parameters.AddWithValue("@w", window.Width);
                windowCmd.Parameters.AddWithValue("@h", window.Height);
                windowCmd.Parameters.AddWithValue("@ss", window.ShowState);
                windowCmd.Parameters.AddWithValue("@ia", window.IsActive ? 1 : 0);
                windowCmd.Parameters.AddWithValue("@sti", window.SelectedTabIndex);
                windowCmd.Parameters.AddWithValue("@ws", (object?)window.Workspace ?? DBNull.Value);
                windowCmd.Parameters.AddWithValue("@an", (object?)window.AppName ?? DBNull.Value);
                windowCmd.Parameters.AddWithValue("@ut", (object?)window.UserTitle ?? DBNull.Value);
                long windowId = (long)windowCmd.ExecuteScalar()!;

                foreach (var tab in window.Tabs)
                {
                    using var tabCmd = _connection.CreateCommand();
                    tabCmd.CommandText = """
                        INSERT INTO tabs (window_id, tab_index, current_url, title, pinned,
                            last_active_time, tab_group_token, extension_app_id, navigation_history)
                        VALUES (@wid, @ti, @url, @title, @pinned, @lat, @tgt, @eai, @nav)
                        """;
                    tabCmd.Parameters.AddWithValue("@wid", windowId);
                    tabCmd.Parameters.AddWithValue("@ti", tab.TabIndex);
                    tabCmd.Parameters.AddWithValue("@url", tab.CurrentUrl);
                    tabCmd.Parameters.AddWithValue("@title", (object?)tab.Title ?? DBNull.Value);
                    tabCmd.Parameters.AddWithValue("@pinned", tab.Pinned ? 1 : 0);
                    tabCmd.Parameters.AddWithValue("@lat",
                        tab.LastActiveTime.HasValue ? (object)tab.LastActiveTime.Value.ToString("O") : DBNull.Value);
                    tabCmd.Parameters.AddWithValue("@tgt", (object?)tab.TabGroupToken ?? DBNull.Value);
                    tabCmd.Parameters.AddWithValue("@eai", (object?)tab.ExtensionAppId ?? DBNull.Value);
                    tabCmd.Parameters.AddWithValue("@nav", JsonSerializer.Serialize(
                        tab.NavigationHistory.Select(n => new
                        {
                            url = n.Url,
                            title = n.Title,
                            timestamp = n.Timestamp?.ToString("O"),
                            referrer = string.IsNullOrEmpty(n.ReferrerUrl) ? null : n.ReferrerUrl,
                            originalUrl = string.IsNullOrEmpty(n.OriginalRequestUrl) ? null : n.OriginalRequestUrl,
                            httpStatus = n.HttpStatusCode > 0 ? n.HttpStatusCode : (int?)null,
                            transition = n.TransitionType,
                            hasPostData = n.HasPostData ? true : (bool?)null
                        }),
                        JsonSerializerOptions.Web));
                    tabCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            _logger.LogInformation("Saved snapshot {Id} with {WindowCount} windows", snapshotId, snapshot.Windows.Count);
            return snapshotId;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Prunes old snapshots according to the retention policy:
    /// - Today: keep all
    /// - Yesterday: keep oldest
    /// - Previous week (2–7 days ago): keep oldest
    /// - Previous month (8–30 days ago): keep oldest
    /// - Older: keep oldest per calendar month
    /// </summary>
    public void PruneSnapshots()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);
        var monthAgo = today.AddDays(-30);

        // Get all snapshots ordered by timestamp
        var snapshots = new List<(long Id, DateTime Timestamp)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, timestamp FROM snapshots ORDER BY timestamp";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var ts = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
                snapshots.Add((id, ts));
            }
        }

        if (snapshots.Count == 0) return;

        var toKeep = new HashSet<long>();

        foreach (var (id, ts) in snapshots)
        {
            var date = ts.Date;

            if (date >= today)
            {
                // Today: keep all
                toKeep.Add(id);
            }
        }

        // Yesterday: keep oldest
        KeepOldest(snapshots.Where(s => s.Timestamp.Date >= yesterday && s.Timestamp.Date < today), toKeep);

        // Previous week (2–7 days ago): keep oldest
        KeepOldest(snapshots.Where(s => s.Timestamp.Date >= weekAgo && s.Timestamp.Date < yesterday), toKeep);

        // Previous month (8–30 days ago): keep oldest
        KeepOldest(snapshots.Where(s => s.Timestamp.Date >= monthAgo && s.Timestamp.Date < weekAgo), toKeep);

        // Older: keep oldest per calendar month
        var olderSnapshots = snapshots.Where(s => s.Timestamp.Date < monthAgo);
        foreach (var monthGroup in olderSnapshots.GroupBy(s => new { s.Timestamp.Year, s.Timestamp.Month }))
        {
            KeepOldest(monthGroup, toKeep);
        }

        // Delete snapshots not in the keep set
        var toDelete = snapshots.Where(s => !toKeep.Contains(s.Id)).Select(s => s.Id).ToList();

        if (toDelete.Count == 0)
        {
            _logger.LogDebug("No snapshots to prune");
            return;
        }

        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var id in toDelete)
            {
                DeleteSnapshot(id);
            }

            transaction.Commit();
            _logger.LogInformation("Pruned {Count} old snapshots, kept {Kept}", toDelete.Count, toKeep.Count);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void KeepOldest(IEnumerable<(long Id, DateTime Timestamp)> snapshots, HashSet<long> toKeep)
    {
        var oldest = snapshots.OrderBy(s => s.Timestamp).FirstOrDefault();
        if (oldest.Id != 0)
            toKeep.Add(oldest.Id);
    }

    private void DeleteSnapshot(long snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM tabs WHERE window_id IN (SELECT id FROM windows WHERE snapshot_id = @sid);
            DELETE FROM windows WHERE snapshot_id = @sid;
            DELETE FROM snapshots WHERE id = @sid;
            """;
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
