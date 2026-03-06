using Microsoft.Data.Sqlite;

namespace TabHistorian.Common;

public record SnapshotInfo(long Id, string Timestamp, int WindowCount, int TabCount);
public record ProfileInfo(string ProfileName, string ProfileDisplayName);

public record TabRow(
    long SnapshotId, string SnapshotTimestamp,
    long WindowId, string ProfileName, string ProfileDisplayName, int WindowIndex,
    int WindowType, int ShowState, bool IsActive,
    int? X, int? Y, int? Width, int? Height,
    int SelectedTabIndex, string? Workspace, string? AppName, string? UserTitle,
    long TabId, int TabIndex, string CurrentUrl, string Title, bool Pinned,
    string? LastActiveTime, string? TabGroupToken, string? ExtensionAppId,
    string NavigationHistory);

public class TabHistorianDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<string> _ignoredProfiles;
    private readonly Dictionary<string, string> _profileDisplayNames;

    public string DbPath { get; }

    public TabHistorianDb(TabHistorianSettings settings)
    {
        DbPath = settings.ResolvedDatabasePath;
        _ignoredProfiles = settings.IgnoredProfiles;
        _profileDisplayNames = settings.ProfileDisplayNames;

        if (!File.Exists(DbPath))
            throw new FileNotFoundException($"Database not found at {DbPath}. Run the TabHistorian service first.");

        // ReadWrite needed for WAL mode (shared memory access), but query_only prevents any writes
        _connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadWrite");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA query_only=ON";
        cmd.ExecuteNonQuery();
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

    public List<ProfileInfo> GetProfiles()
    {
        using var cmd = _connection.CreateCommand();
        var ignFilter = AddIgnoredProfileParams(cmd, "w");
        var where = string.IsNullOrEmpty(ignFilter) ? "" : $"WHERE {ignFilter}";
        cmd.CommandText = $"""
            SELECT DISTINCT w.profile_name, w.profile_display_name
            FROM windows w
            {where}
            """;

        var results = new List<ProfileInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var profileName = reader.GetString(0);
            var dbDisplayName = reader.IsDBNull(1) ? profileName : reader.GetString(1);
            var displayName = _profileDisplayNames.TryGetValue(profileName, out var overrideName)
                ? overrideName : dbDisplayName;
            results.Add(new ProfileInfo(profileName, displayName));
        }
        var isRemote = (ProfileInfo p) => p.ProfileName.StartsWith("synced:");
        var isDefault = (ProfileInfo p) => p.ProfileName == "Default";
        results.Sort((a, b) =>
        {
            var aRemote = isRemote(a);
            var bRemote = isRemote(b);
            if (aRemote != bRemote) return aRemote ? 1 : -1;
            if (!aRemote)
            {
                var aDefault = isDefault(a);
                var bDefault = isDefault(b);
                if (aDefault != bDefault) return aDefault ? -1 : 1;
            }
            return string.Compare(a.ProfileDisplayName, b.ProfileDisplayName, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    private string AddIgnoredProfileParams(SqliteCommand cmd, string alias)
    {
        if (_ignoredProfiles.Count == 0) return "";
        var paramNames = new List<string>();
        for (var i = 0; i < _ignoredProfiles.Count; i++)
        {
            var name = $"@ign{i}";
            paramNames.Add(name);
            cmd.Parameters.AddWithValue(name, _ignoredProfiles[i]);
        }
        return $"{alias}.profile_name NOT IN ({string.Join(", ", paramNames)})";
    }

    public int CountTabs(string? query, long? snapshotId, string? profileName)
    {
        using var cmd = _connection.CreateCommand();
        AddFilterConditions(cmd, query, snapshotId, profileName);
        var where = BuildWhereClause(cmd);

        cmd.CommandText = $"""
            SELECT COUNT(*)
            FROM tabs t
            JOIN windows w ON w.id = t.window_id
            JOIN snapshots s ON s.id = w.snapshot_id
            {where}
            """;

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<TabRow> SearchTabs(string? query, long? snapshotId, string? profileName = null) =>
        SearchTabs(query, snapshotId, profileName, 0, int.MaxValue);

    public List<TabRow> SearchTabs(string? query, long? snapshotId, string? profileName, int offset, int limit)
    {
        using var cmd = _connection.CreateCommand();
        AddFilterConditions(cmd, query, snapshotId, profileName);
        var where = BuildWhereClause(cmd);

        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        cmd.CommandText = $"""
            SELECT s.id, s.timestamp,
                   w.id, w.profile_name, w.profile_display_name, w.window_index,
                   w.window_type, w.show_state, w.is_active,
                   w.x, w.y, w.width, w.height,
                   w.selected_tab_index, w.workspace, w.app_name, w.user_title,
                   t.id, t.tab_index, t.current_url, t.title, t.pinned,
                   t.last_active_time, t.tab_group_token, t.extension_app_id,
                   t.navigation_history
            FROM tabs t
            JOIN windows w ON w.id = t.window_id
            JOIN snapshots s ON s.id = w.snapshot_id
            {where}
            ORDER BY s.timestamp DESC, w.profile_display_name, w.window_index, t.tab_index
            LIMIT @limit OFFSET @offset
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
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetInt64(17), reader.GetInt32(18),
                reader.GetString(19),
                reader.IsDBNull(20) ? "" : reader.GetString(20),
                reader.GetInt32(21) != 0,
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.IsDBNull(24) ? null : reader.GetString(24),
                reader.IsDBNull(25) ? "[]" : reader.GetString(25)));
        }
        return results;
    }

    private static void AddFilterConditions(SqliteCommand cmd, string? query, long? snapshotId, string? profileName)
    {
        if (snapshotId.HasValue)
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId.Value);
        if (!string.IsNullOrWhiteSpace(query))
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
        if (!string.IsNullOrWhiteSpace(profileName))
            cmd.Parameters.AddWithValue("@profileName", profileName);
    }

    private static string BuildWhereClause(SqliteCommand cmd)
    {
        var conditions = new List<string>();
        foreach (SqliteParameter p in cmd.Parameters)
        {
            switch (p.ParameterName)
            {
                case "@snapshotId": conditions.Add("s.id = @snapshotId"); break;
                case "@q": conditions.Add("(t.title LIKE @q OR t.current_url LIKE @q OR t.navigation_history LIKE @q)"); break;
                case "@profileName": conditions.Add("w.profile_name = @profileName"); break;
            }
        }
        return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    }

    public void Dispose() => _connection.Dispose();
}
