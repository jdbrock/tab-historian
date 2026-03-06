using Microsoft.Data.Sqlite;

namespace TabHistorian.Common;

public record TabIdentityRow(
    long Id, string ProfileName, string? ProfileDisplayName,
    string FirstUrl, string FirstTitle, string FirstSeen,
    string LastUrl, string LastTitle, string LastSeen,
    string? LastActiveTime, string? FirstActiveTime, string? LastNavigated,
    int EventCount, bool IsOpen,
    int WindowIndex, int TabIndex);

public record TabEventRow(
    long Id, long TabIdentityId, string EventType, string Timestamp,
    string? StateDelta, string? Url, string? Title, string? ProfileName);

public record ProfileRow(string ProfileName, string? DisplayName);

public record TabMachineStatsRow(
    int TotalTabs, int OpenTabs, int ClosedTabs, int TotalEvents,
    string? FirstSeen, string? LastSeen);

public record CurrentStateRow(
    long TabIdentityId, string CurrentUrl, string Title, bool Pinned,
    string? LastActiveTime, int TabIndex, int WindowIndex,
    string ProfileName, string? ProfileDisplayName,
    string? NavigationHistory, bool IsOpen);

public class TabMachineReader
{
    private readonly string _connectionString;
    private readonly List<string> _ignoredProfiles;
    private readonly Dictionary<string, string> _profileDisplayNames;

    public TabMachineReader(TabHistorianSettings settings)
    {
        var dbPath = settings.ResolvedTabMachineDatabasePath;

        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"TabMachine database not found at {dbPath}. Run the TabHistorian service first.");

        _connectionString = $"Data Source={dbPath};Mode=ReadWrite";
        _ignoredProfiles = settings.IgnoredProfiles;
        _profileDisplayNames = settings.ProfileDisplayNames;
    }

    private string? ResolveDisplayName(string profileName, string? dbDisplayName)
    {
        return _profileDisplayNames.TryGetValue(profileName, out var overrideName)
            ? overrideName
            : dbDisplayName;
    }

    private string AddIgnoredProfileParams(SqliteCommand cmd, string alias = "ti")
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

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA query_only=ON";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public TabMachineStatsRow GetStats()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM tab_identities),
                (SELECT COUNT(*) FROM tab_current_state WHERE is_open = 1),
                (SELECT COUNT(*) FROM tab_current_state WHERE is_open = 0),
                (SELECT COUNT(*) FROM tab_events),
                (SELECT MIN(first_seen) FROM tab_identities),
                (SELECT MAX(last_seen) FROM tab_identities)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new TabMachineStatsRow(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public List<ProfileRow> GetProfiles()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var ignFilter = AddIgnoredProfileParams(cmd);
        var where = string.IsNullOrEmpty(ignFilter) ? "" : $"WHERE {ignFilter}";
        cmd.CommandText = $"""
            SELECT ti.profile_name, cs.profile_display_name
            FROM tab_identities ti
            LEFT JOIN tab_current_state cs ON cs.tab_identity_id = ti.id
            {where}
            GROUP BY ti.profile_name
            ORDER BY ti.profile_name
            """;
        var results = new List<ProfileRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var profileName = reader.GetString(0);
            var dbDisplayName = reader.IsDBNull(1) ? null : reader.GetString(1);
            results.Add(new ProfileRow(profileName, ResolveDisplayName(profileName, dbDisplayName)));
        }
        var isRemote = (ProfileRow p) => p.ProfileName.StartsWith("synced:");
        var isDefault = (ProfileRow p) => p.ProfileName == "Default";
        results.Sort((a, b) =>
        {
            var aRemote = isRemote(a);
            var bRemote = isRemote(b);
            if (aRemote != bRemote) return aRemote ? 1 : -1;
            // Default profile (Personal) always first among local profiles
            if (!aRemote)
            {
                var aDefault = isDefault(a);
                var bDefault = isDefault(b);
                if (aDefault != bDefault) return aDefault ? -1 : 1;
            }
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        return results;
    }

    public int CountSearch(string? query, string? profile, bool? isOpen)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = BuildSearchWhere(cmd, query, profile, isOpen);
        cmd.CommandText = $"""
            SELECT COUNT(*)
            FROM tab_identities ti
            LEFT JOIN tab_current_state cs ON cs.tab_identity_id = ti.id
            {where}
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<TabIdentityRow> Search(string? query, string? profile, bool? isOpen, string? sort, int offset, int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = BuildSearchWhere(cmd, query, profile, isOpen);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var windowJoin = "";
        string orderBy;
        if (sort == "window")
        {
            windowJoin = """
                LEFT JOIN (
                    SELECT cs2.profile_name AS wp, cs2.window_index AS wi,
                           MAX(COALESCE(ti2.last_navigated, ti2.last_active_time, ti2.last_seen)) AS max_nav
                    FROM tab_current_state cs2
                    JOIN tab_identities ti2 ON ti2.id = cs2.tab_identity_id
                    WHERE cs2.is_open = 1
                    GROUP BY cs2.profile_name, cs2.window_index
                ) wn ON wn.wp = cs.profile_name AND wn.wi = cs.window_index
                """;
            orderBy = "ORDER BY wn.max_nav DESC NULLS LAST, cs.profile_name, cs.window_index, ti.last_active_time DESC NULLS LAST";
        }
        else
        {
            orderBy = "ORDER BY ti.last_active_time DESC NULLS LAST, ti.last_seen DESC";
        }

        cmd.CommandText = $"""
            SELECT ti.id, ti.profile_name, cs.profile_display_name,
                   ti.first_url, ti.first_title, ti.first_seen,
                   ti.last_url, ti.last_title, ti.last_seen, ti.last_active_time,
                   ti.first_active_time, ti.last_navigated,
                   (SELECT COUNT(*) FROM tab_events te WHERE te.tab_identity_id = ti.id) as event_count,
                   COALESCE(cs.is_open, 0),
                   COALESCE(cs.window_index, 0),
                   COALESCE(cs.tab_index, 0)
            FROM tab_identities ti
            LEFT JOIN tab_current_state cs ON cs.tab_identity_id = ti.id
            {windowJoin}
            {where}
            {orderBy}
            LIMIT @limit OFFSET @offset
            """;

        var results = new List<TabIdentityRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var profileName = reader.GetString(1);
            results.Add(new TabIdentityRow(
                reader.GetInt64(0), profileName,
                ResolveDisplayName(profileName, reader.IsDBNull(2) ? null : reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetInt32(12),
                reader.GetInt32(13) != 0,
                reader.GetInt32(14),
                reader.GetInt32(15)));
        }
        return results;
    }

    public List<TabEventRow> GetEvents(long? tabIdentityId, string? eventType, string? before, string? after, int offset, int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        if (tabIdentityId.HasValue)
        {
            cmd.Parameters.AddWithValue("@tabIdentityId", tabIdentityId.Value);
            conditions.Add("te.tab_identity_id = @tabIdentityId");
        }
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            cmd.Parameters.AddWithValue("@eventType", eventType);
            conditions.Add("te.event_type = @eventType");
        }
        if (!string.IsNullOrWhiteSpace(before))
        {
            cmd.Parameters.AddWithValue("@before", before);
            conditions.Add("te.timestamp <= @before");
        }
        if (!string.IsNullOrWhiteSpace(after))
        {
            cmd.Parameters.AddWithValue("@after", after);
            conditions.Add("te.timestamp >= @after");
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        cmd.CommandText = $"""
            SELECT te.id, te.tab_identity_id, te.event_type, te.timestamp,
                   te.state_delta, te.url, te.title, te.profile_name
            FROM tab_events te
            {where}
            ORDER BY te.timestamp DESC
            LIMIT @limit OFFSET @offset
            """;

        var results = new List<TabEventRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TabEventRow(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return results;
    }

    public int CountEvents(long? tabIdentityId, string? eventType, string? before, string? after)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        if (tabIdentityId.HasValue)
        {
            cmd.Parameters.AddWithValue("@tabIdentityId", tabIdentityId.Value);
            conditions.Add("te.tab_identity_id = @tabIdentityId");
        }
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            cmd.Parameters.AddWithValue("@eventType", eventType);
            conditions.Add("te.event_type = @eventType");
        }
        if (!string.IsNullOrWhiteSpace(before))
        {
            cmd.Parameters.AddWithValue("@before", before);
            conditions.Add("te.timestamp <= @before");
        }
        if (!string.IsNullOrWhiteSpace(after))
        {
            cmd.Parameters.AddWithValue("@after", after);
            conditions.Add("te.timestamp >= @after");
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        cmd.CommandText = $"""
            SELECT COUNT(*)
            FROM tab_events te
            {where}
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<CurrentStateRow> GetTimeline(string timestamp, string? profile)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("@timestamp", timestamp);

        var profileFilter = "";
        if (!string.IsNullOrWhiteSpace(profile))
        {
            cmd.Parameters.AddWithValue("@profile", profile);
            profileFilter = "AND ti.profile_name = @profile";
        }
        var ignFilter = AddIgnoredProfileParams(cmd);
        if (!string.IsNullOrEmpty(ignFilter))
            profileFilter += $" AND {ignFilter}";

        cmd.CommandText = $"""
            SELECT cs.tab_identity_id, cs.current_url, cs.title, cs.pinned,
                   cs.last_active_time, cs.tab_index, cs.window_index,
                   cs.profile_name, cs.profile_display_name,
                   cs.navigation_history, cs.is_open
            FROM tab_identities ti
            JOIN tab_current_state cs ON cs.tab_identity_id = ti.id
            WHERE ti.first_seen <= @timestamp
              AND NOT EXISTS (
                  SELECT 1 FROM tab_events te
                  WHERE te.tab_identity_id = ti.id
                    AND te.event_type = 'Closed'
                    AND te.timestamp <= @timestamp
              )
              {profileFilter}
            ORDER BY cs.profile_name, cs.window_index, cs.tab_index
            """;

        var results = new List<CurrentStateRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var pn = reader.GetString(7);
            results.Add(new CurrentStateRow(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                !reader.IsDBNull(3) && reader.GetInt32(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                pn,
                ResolveDisplayName(pn, reader.IsDBNull(8) ? null : reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                !reader.IsDBNull(10) && reader.GetInt32(10) != 0));
        }
        return results;
    }

    private string BuildSearchWhere(SqliteCommand cmd, string? query, string? profile, bool? isOpen)
    {
        var conditions = new List<string>();

        var ignFilter = AddIgnoredProfileParams(cmd);
        if (!string.IsNullOrEmpty(ignFilter))
            conditions.Add(ignFilter);

        // Exclude tabs whose entire history is chrome-internal URLs
        conditions.Add("""
            NOT (
                (ti.first_url LIKE 'chrome://%' OR ti.first_url LIKE 'chrome-extension://%' OR ti.first_url = 'chrome://newtab/')
                AND (ti.last_url LIKE 'chrome://%' OR ti.last_url LIKE 'chrome-extension://%' OR ti.last_url = 'chrome://newtab/')
            )
            """);

        if (!string.IsNullOrWhiteSpace(query))
        {
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
            conditions.Add("(ti.last_url LIKE @q OR ti.last_title LIKE @q OR ti.first_url LIKE @q OR ti.first_title LIKE @q)");
        }
        if (!string.IsNullOrWhiteSpace(profile))
        {
            cmd.Parameters.AddWithValue("@profile", profile);
            conditions.Add("ti.profile_name = @profile");
        }
        if (isOpen.HasValue)
        {
            cmd.Parameters.AddWithValue("@isOpen", isOpen.Value ? 1 : 0);
            conditions.Add("COALESCE(cs.is_open, 0) = @isOpen");
        }

        return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    }
}
