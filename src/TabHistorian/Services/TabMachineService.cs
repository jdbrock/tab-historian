using System.Text.Json;
using Microsoft.Data.Sqlite;
using TabHistorian.Models;

namespace TabHistorian.Services;

public class TabMachineService
{
    private readonly TabMachineDb _db;
    private readonly ILogger<TabMachineService> _logger;

    public TabMachineService(TabMachineDb db, ILogger<TabMachineService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private record SnapshotTab(
        string ProfileName,
        string ProfileDisplayName,
        string Url,
        string Title,
        bool Pinned,
        string? LastActiveTime,
        int WindowIndex,
        int TabIndex,
        int WindowType,
        int ShowState,
        bool IsActive,
        string? SyncTabNodeId,
        string? TabGroupToken,
        string? ExtensionAppId,
        string NavigationHistoryJson,
        List<string> NavUrls);

    private record CurrentStateRow(
        long TabIdentityId,
        string ProfileName,
        string ProfileDisplayName,
        string Url,
        string Title,
        bool Pinned,
        string? LastActiveTime,
        int WindowIndex,
        int TabIndex,
        int WindowType,
        int ShowState,
        bool IsActive,
        string? SyncTabNodeId,
        string? TabGroupToken,
        string? ExtensionAppId,
        string NavigationHistoryJson,
        List<string> NavUrls);

    public void ProcessSnapshot(List<ChromeWindow> windows, DateTime timestamp)
    {
        var conn = _db.Connection;
        _logger.LogInformation("Tab Machine: building snapshot tabs from {Windows} windows", windows.Count);
        var currentTabs = BuildSnapshotTabs(windows);
        _logger.LogInformation("Tab Machine: {Count} tabs in current snapshot", currentTabs.Count);

        var previousState = LoadCurrentState();
        _logger.LogInformation("Tab Machine: {Count} open tabs in previous state", previousState.Count);

        if (previousState.Count == 0)
        {
            _logger.LogInformation("First Tab Machine run — recording {Count} tabs as opened", currentTabs.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var tab in currentTabs)
                {
                    var identityId = CreateTabIdentity(tab, timestamp);
                    var fullState = BuildFullState(tab);
                    RecordEvent(identityId, TabEventType.Opened, timestamp, fullState, tab);
                    UpsertCurrentState(identityId, tab, isOpen: true);
                }
                transaction.Commit();
                _logger.LogInformation("Tab Machine: first run committed in {Elapsed}ms", sw.ElapsedMilliseconds);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            return;
        }

        DiffAndRecordEvents(previousState, currentTabs, timestamp);
    }

    private static List<SnapshotTab> BuildSnapshotTabs(List<ChromeWindow> windows)
    {
        var tabs = new List<SnapshotTab>();
        foreach (var window in windows)
        {
            foreach (var tab in window.Tabs)
            {
                var navJson = JsonSerializer.Serialize(
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
                    JsonSerializerOptions.Web);

                var navUrls = tab.NavigationHistory
                    .Select(n => n.Url)
                    .Where(u => !string.IsNullOrEmpty(u))
                    .ToList();

                tabs.Add(new SnapshotTab(
                    ProfileName: window.ProfileName,
                    ProfileDisplayName: window.ProfileDisplayName,
                    Url: tab.CurrentUrl,
                    Title: tab.Title,
                    Pinned: tab.Pinned,
                    LastActiveTime: tab.LastActiveTime?.ToString("O"),
                    WindowIndex: window.WindowIndex,
                    TabIndex: tab.TabIndex,
                    WindowType: window.WindowType,
                    ShowState: window.ShowState,
                    IsActive: window.IsActive,
                    SyncTabNodeId: tab.SyncTabNodeId,
                    TabGroupToken: tab.TabGroupToken,
                    ExtensionAppId: tab.ExtensionAppId,
                    NavigationHistoryJson: navJson,
                    NavUrls: navUrls));
            }
        }
        return tabs;
    }

    private List<CurrentStateRow> LoadCurrentState()
    {
        var rows = new List<CurrentStateRow>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT cs.tab_identity_id, cs.profile_name, cs.profile_display_name,
                   cs.current_url, cs.title, cs.pinned, cs.last_active_time,
                   cs.window_index, cs.tab_index, cs.window_type, cs.show_state,
                   cs.is_active, cs.sync_tab_node_id, cs.tab_group_token,
                   cs.extension_app_id, cs.navigation_history
            FROM tab_current_state cs
            WHERE cs.is_open = 1
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var navJson = reader.IsDBNull(15) ? "[]" : reader.GetString(15);
            var navUrls = ExtractNavUrls(navJson);

            rows.Add(new CurrentStateRow(
                TabIdentityId: reader.GetInt64(0),
                ProfileName: reader.GetString(1),
                ProfileDisplayName: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Url: reader.GetString(3),
                Title: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Pinned: !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                LastActiveTime: reader.IsDBNull(6) ? null : reader.GetString(6),
                WindowIndex: reader.GetInt32(7),
                TabIndex: reader.GetInt32(8),
                WindowType: reader.GetInt32(9),
                ShowState: reader.GetInt32(10),
                IsActive: !reader.IsDBNull(11) && reader.GetInt32(11) != 0,
                SyncTabNodeId: reader.IsDBNull(12) ? null : reader.GetString(12),
                TabGroupToken: reader.IsDBNull(13) ? null : reader.GetString(13),
                ExtensionAppId: reader.IsDBNull(14) ? null : reader.GetString(14),
                NavigationHistoryJson: navJson,
                NavUrls: navUrls));
        }
        return rows;
    }

    private void DiffAndRecordEvents(
        List<CurrentStateRow> previousState,
        List<SnapshotTab> currentTabs,
        DateTime timestamp)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (matched, opened, closed) = MatchTabs(previousState, currentTabs);
        _logger.LogInformation(
            "Tab Machine matching: {Matched} matched, {Opened} new, {Closed} gone (took {Elapsed}ms)",
            matched.Count, opened.Count, closed.Count, sw.ElapsedMilliseconds);

        var conn = _db.Connection;

        using var transaction = conn.BeginTransaction();
        try
        {
            // Opened tabs
            foreach (var tab in opened)
            {
                var identityId = CreateTabIdentity(tab, timestamp);
                var fullState = BuildFullState(tab);
                RecordEvent(identityId, TabEventType.Opened, timestamp, fullState, tab);
                UpsertCurrentState(identityId, tab, isOpen: true);
            }

            // Closed tabs
            foreach (var prev in closed)
            {
                RecordEvent(prev.TabIdentityId, TabEventType.Closed, timestamp, null, null);
                MarkClosed(prev.TabIdentityId);
            }

            // Matched — compute deltas
            var eventCounts = new Dictionary<TabEventType, int>();
            int unchangedCount = 0;
            int silentUpdateCount = 0;
            foreach (var (prev, curr) in matched)
            {
                var delta = ComputeDelta(prev, curr);
                if (delta == null)
                {
                    unchangedCount++;
                    continue;
                }

                // Always update current state with all changes (including positional)
                UpsertCurrentState(prev.TabIdentityId, curr, isOpen: true);

                // Only record events for meaningful changes
                var meaningfulDelta = FilterMeaningfulDelta(delta);
                if (meaningfulDelta == null)
                {
                    silentUpdateCount++;
                    continue;
                }

                var eventType = SelectEventType(prev, curr, meaningfulDelta);
                eventCounts[eventType] = eventCounts.GetValueOrDefault(eventType) + 1;
                var deltaJson = JsonSerializer.Serialize(meaningfulDelta, JsonSerializerOptions.Web);
                RecordEvent(prev.TabIdentityId, eventType, timestamp, deltaJson, curr);
                var lastNavTime = delta.ContainsKey("navigationHistory") ? GetLastRealNavTimestamp(curr.NavigationHistoryJson) : null;
                UpdateTabIdentity(prev.TabIdentityId, curr, timestamp, lastNavTime);
            }

            transaction.Commit();

            var totalUpdated = eventCounts.Values.Sum();
            _logger.LogInformation(
                "Tab Machine: {Opened} opened, {Closed} closed, {Updated} updated, {SilentUpdated} silent, {Unchanged} unchanged (committed in {Elapsed}ms)",
                opened.Count, closed.Count, totalUpdated, silentUpdateCount, unchangedCount, sw.ElapsedMilliseconds);

            if (eventCounts.Count > 0)
            {
                var breakdown = string.Join(", ", eventCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
                _logger.LogInformation("Tab Machine event breakdown: {Breakdown}", breakdown);
            }

            // Log sample of opened/closed for debugging
            foreach (var tab in opened.Take(5))
                _logger.LogDebug("Tab Machine [Opened]: {Profile} — {Url}", tab.ProfileName, tab.Url);
            foreach (var prev in closed.Take(5))
                _logger.LogDebug("Tab Machine [Closed]: {Profile} — {Url} (identity {Id})", prev.ProfileName, prev.Url, prev.TabIdentityId);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // Chrome prefixes sleeping/frozen tab titles with "💤 " — strip it so waking tabs don't generate spurious TitleChanged events
    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        return title.StartsWith("\U0001F4A4 ") ? title[3..] :
               title.StartsWith("\U0001F4A4") ? title[2..] : title;
    }

    private static Dictionary<string, object?>? ComputeDelta(CurrentStateRow prev, SnapshotTab curr)
    {
        var delta = new Dictionary<string, object?>();

        if (prev.Url != curr.Url) delta["url"] = curr.Url;
        if (NormalizeTitle(prev.Title) != NormalizeTitle(curr.Title)) delta["title"] = curr.Title;
        if (prev.Pinned != curr.Pinned) delta["pinned"] = curr.Pinned;
        if (prev.LastActiveTime != curr.LastActiveTime) delta["lastActiveTime"] = curr.LastActiveTime;
        if (prev.WindowIndex != curr.WindowIndex) delta["windowIndex"] = curr.WindowIndex;
        if (prev.TabIndex != curr.TabIndex) delta["tabIndex"] = curr.TabIndex;
        if (prev.WindowType != curr.WindowType) delta["windowType"] = curr.WindowType;
        if (prev.ShowState != curr.ShowState) delta["showState"] = curr.ShowState;
        if (prev.IsActive != curr.IsActive) delta["isActive"] = curr.IsActive;
        if (prev.SyncTabNodeId != curr.SyncTabNodeId) delta["syncTabNodeId"] = curr.SyncTabNodeId;
        if (prev.TabGroupToken != curr.TabGroupToken) delta["tabGroupToken"] = curr.TabGroupToken;
        if (prev.ExtensionAppId != curr.ExtensionAppId) delta["extensionAppId"] = curr.ExtensionAppId;
        if (prev.ProfileDisplayName != curr.ProfileDisplayName) delta["profileDisplayName"] = curr.ProfileDisplayName;
        if (prev.NavigationHistoryJson != curr.NavigationHistoryJson) delta["navigationHistory"] = curr.NavigationHistoryJson;

        return delta.Count == 0 ? null : delta;
    }

    // Fields that warrant recording an event in tab_events
    private static readonly HashSet<string> MeaningfulFields =
        ["url", "title", "pinned", "navigationHistory", "tabGroupToken", "extensionAppId"];

    private static Dictionary<string, object?>? FilterMeaningfulDelta(Dictionary<string, object?> delta)
    {
        var meaningful = delta.Where(kv => MeaningfulFields.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return meaningful.Count == 0 ? null : meaningful;
    }

    private static TabEventType SelectEventType(CurrentStateRow prev, SnapshotTab curr, Dictionary<string, object?> delta)
    {
        if (delta.ContainsKey("url")) return TabEventType.Navigated;
        if (delta.ContainsKey("title")) return TabEventType.TitleChanged;
        if (delta.ContainsKey("pinned")) return curr.Pinned ? TabEventType.Pinned : TabEventType.Unpinned;
        return TabEventType.Updated;
    }

    private static string BuildFullState(SnapshotTab tab)
    {
        return JsonSerializer.Serialize(new
        {
            url = tab.Url,
            title = tab.Title,
            pinned = tab.Pinned,
            lastActiveTime = tab.LastActiveTime,
            windowIndex = tab.WindowIndex,
            tabIndex = tab.TabIndex,
            windowType = tab.WindowType,
            showState = tab.ShowState,
            isActive = tab.IsActive,
            syncTabNodeId = tab.SyncTabNodeId,
            tabGroupToken = tab.TabGroupToken,
            extensionAppId = tab.ExtensionAppId,
            profileName = tab.ProfileName,
            profileDisplayName = tab.ProfileDisplayName,
            navigationHistory = tab.NavigationHistoryJson
        }, JsonSerializerOptions.Web);
    }

    #region Matching

    private (List<(CurrentStateRow Prev, SnapshotTab Curr)> Matched,
                     List<SnapshotTab> Opened,
                     List<CurrentStateRow> Closed)
        MatchTabs(List<CurrentStateRow> previous, List<SnapshotTab> current)
    {
        var matched = new List<(CurrentStateRow, SnapshotTab)>();
        var unmatchedPrev = new List<CurrentStateRow>(previous);
        var unmatchedCurr = new List<SnapshotTab>(current);

        // Pass 0: SyncTabNodeId exact match
        MatchByPredicate(unmatchedPrev, unmatchedCurr, matched,
            (p, c) => p.SyncTabNodeId != null
                    && p.SyncTabNodeId == c.SyncTabNodeId);
        _logger.LogDebug("Tab Machine Pass 0 (SyncTabNodeId): {Matched} matched, {Prev} prev remain, {Curr} curr remain",
            matched.Count, unmatchedPrev.Count, unmatchedCurr.Count);

        static bool allowHeuristic(CurrentStateRow p, SnapshotTab c)
            => p.SyncTabNodeId == null || c.SyncTabNodeId == null;

        int prevMatched = matched.Count;

        // Pass 1: Exact nav history
        MatchByPredicate(unmatchedPrev, unmatchedCurr, matched,
            (p, c) => allowHeuristic(p, c)
                    && p.ProfileName == c.ProfileName
                    && p.NavUrls.Count >= 2
                    && p.NavUrls.Count == c.NavUrls.Count
                    && p.NavUrls.SequenceEqual(c.NavUrls));
        _logger.LogDebug("Tab Machine Pass 1 (exact nav): +{New} matched", matched.Count - prevMatched);
        prevMatched = matched.Count;

        // Pass 2: Nav history prefix
        MatchByPredicate(unmatchedPrev, unmatchedCurr, matched,
            (p, c) => allowHeuristic(p, c)
                    && p.ProfileName == c.ProfileName
                    && p.NavUrls.Count >= 2
                    && c.NavUrls.Count > p.NavUrls.Count
                    && c.NavUrls.Take(p.NavUrls.Count).SequenceEqual(p.NavUrls));
        _logger.LogDebug("Tab Machine Pass 2 (nav prefix): +{New} matched", matched.Count - prevMatched);
        prevMatched = matched.Count;

        // Pass 3: Single-nav-entry by profile + URL
        MatchByPredicate(unmatchedPrev, unmatchedCurr, matched,
            (p, c) => allowHeuristic(p, c)
                    && p.ProfileName == c.ProfileName
                    && p.NavUrls.Count <= 1
                    && c.NavUrls.Count <= 1
                    && p.Url == c.Url);
        _logger.LogDebug("Tab Machine Pass 3 (single URL): +{New} matched", matched.Count - prevMatched);

        return (matched, unmatchedCurr, unmatchedPrev);
    }

    private static void MatchByPredicate(
        List<CurrentStateRow> unmatchedPrev,
        List<SnapshotTab> unmatchedCurr,
        List<(CurrentStateRow, SnapshotTab)> matched,
        Func<CurrentStateRow, SnapshotTab, bool> predicate)
    {
        for (int i = unmatchedCurr.Count - 1; i >= 0; i--)
        {
            var curr = unmatchedCurr[i];
            for (int j = unmatchedPrev.Count - 1; j >= 0; j--)
            {
                if (predicate(unmatchedPrev[j], curr))
                {
                    matched.Add((unmatchedPrev[j], curr));
                    unmatchedPrev.RemoveAt(j);
                    unmatchedCurr.RemoveAt(i);
                    break;
                }
            }
        }
    }

    #endregion

    #region Database operations

    private long CreateTabIdentity(SnapshotTab tab, DateTime timestamp)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tab_identities (profile_name, first_url, first_title, first_seen,
                last_url, last_title, last_seen, last_active_time, first_active_time, last_navigated)
            VALUES (@pn, @fu, @ft, @fs, @lu, @lt, @ls, @lat, @fat, @ln)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@pn", tab.ProfileName);
        cmd.Parameters.AddWithValue("@fu", tab.Url);
        cmd.Parameters.AddWithValue("@ft", tab.Title);
        cmd.Parameters.AddWithValue("@fs", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@lu", tab.Url);
        cmd.Parameters.AddWithValue("@lt", tab.Title);
        cmd.Parameters.AddWithValue("@ls", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@lat", (object?)tab.LastActiveTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fat", (object?)tab.LastActiveTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ln", (object?)GetLastRealNavTimestamp(tab.NavigationHistoryJson) ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    // Extract the timestamp of the last real navigation entry, skipping sleep/wake transitions
    // (entries where the URL didn't change and the title only differs by the 💤 prefix)
    private static string? GetLastRealNavTimestamp(string? navHistoryJson)
    {
        if (string.IsNullOrEmpty(navHistoryJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(navHistoryJson);
            string? lastTs = null;
            string? prevUrl = null;
            string? prevTitle = null;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var url = entry.GetProperty("url").GetString() ?? "";
                var title = entry.GetProperty("title").GetString() ?? "";
                var ts = entry.GetProperty("timestamp").GetString();

                // If URL matches previous entry, check if it's just a sleep/wake title change
                if (prevUrl != null && url == prevUrl && IsSleepWakeTitleChange(prevTitle, title))
                {
                    prevTitle = title;
                    continue;
                }

                if (ts != null) lastTs = ts;
                prevUrl = url;
                prevTitle = title;
            }
            return lastTs;
        }
        catch { return null; }
    }

    // Returns true if the only difference between two titles is the 💤 prefix
    private static bool IsSleepWakeTitleChange(string? a, string? b)
    {
        if (a == null || b == null) return false;
        return NormalizeTitle(a) == NormalizeTitle(b);
    }

    private void UpdateTabIdentity(long identityId, SnapshotTab tab, DateTime timestamp, string? lastNavTime)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = lastNavTime != null
            ? """
              UPDATE tab_identities
              SET last_url = @lu, last_title = @lt, last_seen = @ls, last_active_time = @lat, last_navigated = @ln
              WHERE id = @id
              """
            : """
              UPDATE tab_identities
              SET last_url = @lu, last_title = @lt, last_seen = @ls, last_active_time = @lat
              WHERE id = @id
              """;
        cmd.Parameters.AddWithValue("@lu", tab.Url);
        cmd.Parameters.AddWithValue("@lt", tab.Title);
        cmd.Parameters.AddWithValue("@ls", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@lat", (object?)tab.LastActiveTime ?? DBNull.Value);
        if (lastNavTime != null)
            cmd.Parameters.AddWithValue("@ln", lastNavTime);
        cmd.Parameters.AddWithValue("@id", identityId);
        cmd.ExecuteNonQuery();
    }

    private void RecordEvent(long identityId, TabEventType eventType,
        DateTime timestamp, string? stateDelta, SnapshotTab? tab)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tab_events (tab_identity_id, event_type, timestamp, state_delta, url, title, profile_name)
            VALUES (@tid, @et, @ts, @sd, @url, @title, @pn)
            """;
        cmd.Parameters.AddWithValue("@tid", identityId);
        cmd.Parameters.AddWithValue("@et", eventType.ToString());
        cmd.Parameters.AddWithValue("@ts", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@sd", (object?)stateDelta ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)tab?.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@title", (object?)tab?.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pn", (object?)tab?.ProfileName ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void UpsertCurrentState(long identityId, SnapshotTab tab, bool isOpen)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO tab_current_state
                (tab_identity_id, current_url, title, pinned, last_active_time,
                 tab_index, window_index, window_type, profile_name, profile_display_name,
                 sync_tab_node_id, tab_group_token, extension_app_id, navigation_history,
                 show_state, is_active, is_open)
            VALUES (@id, @url, @title, @pinned, @lat, @ti, @wi, @wt, @pn, @pdn,
                    @stn, @tgt, @eai, @nav, @ss, @ia, @io)
            """;
        cmd.Parameters.AddWithValue("@id", identityId);
        cmd.Parameters.AddWithValue("@url", tab.Url);
        cmd.Parameters.AddWithValue("@title", tab.Title);
        cmd.Parameters.AddWithValue("@pinned", tab.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("@lat", (object?)tab.LastActiveTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ti", tab.TabIndex);
        cmd.Parameters.AddWithValue("@wi", tab.WindowIndex);
        cmd.Parameters.AddWithValue("@wt", tab.WindowType);
        cmd.Parameters.AddWithValue("@pn", tab.ProfileName);
        cmd.Parameters.AddWithValue("@pdn", tab.ProfileDisplayName);
        cmd.Parameters.AddWithValue("@stn", (object?)tab.SyncTabNodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tgt", (object?)tab.TabGroupToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@eai", (object?)tab.ExtensionAppId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nav", tab.NavigationHistoryJson);
        cmd.Parameters.AddWithValue("@ss", tab.ShowState);
        cmd.Parameters.AddWithValue("@ia", tab.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@io", isOpen ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private void MarkClosed(long identityId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE tab_current_state SET is_open = 0 WHERE tab_identity_id = @id";
        cmd.Parameters.AddWithValue("@id", identityId);
        cmd.ExecuteNonQuery();
    }

    #endregion

    private static List<string> ExtractNavUrls(string navJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(navJson);
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "")
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
