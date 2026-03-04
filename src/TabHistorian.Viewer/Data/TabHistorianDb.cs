using System.IO;
using Microsoft.Data.Sqlite;

namespace TabHistorian.Viewer.Data;

public record SnapshotInfo(long Id, string Timestamp, int WindowCount, int TabCount);

public record TabRow(
    long SnapshotId, string SnapshotTimestamp,
    long WindowId, string ProfileName, string ProfileDisplayName, int WindowIndex,
    int WindowType, int ShowState, bool IsActive,
    long TabId, int TabIndex, string CurrentUrl, string Title, bool Pinned,
    string? LastActiveTime, string NavigationHistory);

public class TabHistorianDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public TabHistorianDb()
    {
        var defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TabHistorian");
        var dbPath = Path.Combine(defaultDir, "tabhistorian.db");

        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Database not found at {dbPath}. Run the TabHistorian service first.");

        _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        _connection.Open();
    }

    public List<SnapshotInfo> GetSnapshots()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.timestamp,
                   COUNT(DISTINCT w.id) as window_count,
                   COUNT(t.id) as tab_count
            FROM snapshots s
            LEFT JOIN windows w ON w.snapshot_id = s.id
            LEFT JOIN tabs t ON t.window_id = w.id
            GROUP BY s.id
            ORDER BY s.timestamp DESC
            """;

        var results = new List<SnapshotInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SnapshotInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }
        return results;
    }

    public List<TabRow> SearchTabs(string? query, long? snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        var conditions = new List<string>();
        if (snapshotId.HasValue)
        {
            conditions.Add("s.id = @snapshotId");
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId.Value);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            conditions.Add("(t.title LIKE @q OR t.current_url LIKE @q OR t.navigation_history LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT s.id, s.timestamp,
                   w.id, w.profile_name, w.profile_display_name, w.window_index,
                   w.window_type, w.show_state, w.is_active,
                   t.id, t.tab_index, t.current_url, t.title, t.pinned,
                   t.last_active_time, t.navigation_history
            FROM tabs t
            JOIN windows w ON w.id = t.window_id
            JOIN snapshots s ON s.id = w.snapshot_id
            {where}
            ORDER BY s.timestamp DESC, w.profile_display_name, w.window_index, t.tab_index
            """;

        var results = new List<TabRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TabRow(
                reader.GetInt64(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetString(3),
                reader.IsDBNull(4) ? reader.GetString(3) : reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
                reader.GetInt64(9), reader.GetInt32(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? "" : reader.GetString(12),
                reader.GetInt32(13) != 0,
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? "[]" : reader.GetString(15)));
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
