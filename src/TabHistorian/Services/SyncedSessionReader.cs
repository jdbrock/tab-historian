using TabHistorian.Models;
using TabHistorian.Parsing;
using static TabHistorian.Parsing.SyncSessionParser;

namespace TabHistorian.Services;

/// <summary>
/// Reads Chrome synced tabs from the Sync Data LevelDB.
/// Each synced device becomes a profile (synced:session_tag).
/// All access to Chrome files is strictly READ-ONLY; LevelDB is copied to temp first.
/// </summary>
public class SyncedSessionReader
{
    private const string SyncDataSubPath = "Sync Data";
    private const string LevelDbSubDir = "LevelDB";
    private const string SessionKeyPrefix = "sessions-dt-";

    private readonly ChromeProfileDiscovery _profileDiscovery;
    private readonly SyncSessionParser _parser = new();
    private readonly ILogger<SyncedSessionReader> _logger;

    public SyncedSessionReader(ChromeProfileDiscovery profileDiscovery, ILogger<SyncedSessionReader> logger)
    {
        _profileDiscovery = profileDiscovery;
        _logger = logger;
    }

    public List<ChromeWindow> ReadSyncedSessions()
    {
        var profiles = _profileDiscovery.DiscoverProfiles();

        // All profiles share the same sync data — use the first one that has it
        string? syncLevelDbPath = null;
        foreach (var profile in profiles)
        {
            var candidatePath = Path.Combine(profile.FullPath, SyncDataSubPath, LevelDbSubDir);
            if (Directory.Exists(candidatePath))
            {
                syncLevelDbPath = candidatePath;
                _logger.LogDebug("Found sync LevelDB in profile {Profile} at {Path}",
                    profile.DisplayName, candidatePath);
                break;
            }
        }

        if (syncLevelDbPath == null)
        {
            _logger.LogDebug("No Sync Data LevelDB found in any profile");
            return [];
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"tabhistorian_sync_{Guid.NewGuid()}");
        try
        {
            CopyLevelDb(syncLevelDbPath, tempDir);
            return ReadFromLevelDb(tempDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read synced sessions from {Path}", syncLevelDbPath);
            return [];
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }
    }

    private void CopyLevelDb(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            try
            {
                using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var dstStream = new FileStream(destFile, FileMode.Create, FileAccess.Write);
                srcStream.CopyTo(dstStream);
            }
            catch (IOException ex)
            {
                // LOCK file or other file Chrome holds exclusively — skip it
                _logger.LogDebug("Could not copy {File}: {Error}", Path.GetFileName(file), ex.Message);
            }
        }
    }

    private List<ChromeWindow> ReadFromLevelDb(string dbPath)
    {
        var headers = new Dictionary<string, ParsedHeader>();
        var tabsByTag = new Dictionary<string, Dictionary<int, ParsedTab>>();

        var allEntries = LevelDbReader.ReadAllWithPrefix(dbPath, SessionKeyPrefix);
        _logger.LogDebug("LevelDB reader returned {Count} entries with prefix {Prefix}",
            allEntries.Count, SessionKeyPrefix);

        int parseFailures = 0;
        foreach (var (keyStr, value) in allEntries)
        {
            var (specifics, error) = _parser.TryParseWithError(value);
            if (specifics == null)
            {
                parseFailures++;
                if (parseFailures <= 3)
                    _logger.LogDebug("Parse fail: ({Len} bytes), error={Error}", value.Length, error);
                continue;
            }

            if (specifics.Header != null)
            {
                headers[specifics.SessionTag] = specifics.Header;
                _logger.LogDebug("Synced device: tag={Tag}, name={Name}, windows={Windows}",
                    specifics.SessionTag, specifics.Header.ClientName, specifics.Header.Windows.Count);
            }

            if (specifics.Tab != null)
            {
                if (!tabsByTag.TryGetValue(specifics.SessionTag, out var tabs))
                {
                    tabs = new Dictionary<int, ParsedTab>();
                    tabsByTag[specifics.SessionTag] = tabs;
                }
                tabs[specifics.Tab.TabId] = specifics.Tab;
            }
        }

        if (parseFailures > 0)
            _logger.LogDebug("{Failures} entries failed to parse as SessionSpecifics", parseFailures);

        _logger.LogDebug("Found {Devices} synced devices with {Tabs} total tab entries",
            headers.Count, tabsByTag.Values.Sum(t => t.Count));

        return AssembleWindows(headers, tabsByTag);
    }

    private List<ChromeWindow> AssembleWindows(
        Dictionary<string, ParsedHeader> headers,
        Dictionary<string, Dictionary<int, ParsedTab>> tabsByTag)
    {
        var result = new List<ChromeWindow>();

        foreach (var (sessionTag, header) in headers)
        {
            var profileName = $"synced:{sessionTag}";
            var profileDisplayName = "Remote: " + (string.IsNullOrEmpty(header.ClientName)
                ? sessionTag
                : header.ClientName);

            tabsByTag.TryGetValue(sessionTag, out var tabsForDevice);
            int windowIndex = 0;

            foreach (var syncWindow in header.Windows)
            {
                var chromeTabs = new List<ChromeTab>();
                int tabIndex = 0;

                foreach (var tabNodeId in syncWindow.TabNodeIds)
                {
                    if (tabsForDevice == null || !tabsForDevice.TryGetValue(tabNodeId, out var syncTab))
                        continue;

                    var navEntries = syncTab.Navigations
                        .Where(n => !string.IsNullOrEmpty(n.Url))
                        .Select(n => new NavigationEntry
                        {
                            Url = n.Url,
                            Title = n.Title,
                            ReferrerUrl = n.Referrer,
                            TransitionType = n.PageTransition & 0xFF,
                            Timestamp = UnixMillisToDateTime(n.TimestampMsec),
                            HttpStatusCode = n.HttpStatusCode
                        })
                        .ToList();

                    string currentUrl = "";
                    string currentTitle = "";
                    int navIdx = syncTab.CurrentNavigationIndex;
                    if (navIdx >= 0 && navIdx < syncTab.Navigations.Count)
                    {
                        currentUrl = syncTab.Navigations[navIdx].Url;
                        currentTitle = syncTab.Navigations[navIdx].Title;
                    }
                    else if (navEntries.Count > 0)
                    {
                        currentUrl = navEntries[^1].Url;
                        currentTitle = navEntries[^1].Title;
                    }

                    if (string.IsNullOrEmpty(currentUrl))
                        continue;

                    chromeTabs.Add(new ChromeTab
                    {
                        TabIndex = tabIndex++,
                        CurrentUrl = currentUrl,
                        Title = currentTitle,
                        Pinned = syncTab.Pinned,
                        LastActiveTime = UnixMillisToDateTime(syncTab.LastActiveTimeMillis),
                        ExtensionAppId = syncTab.ExtensionAppId,
                        NavigationHistory = navEntries
                    });
                }

                if (chromeTabs.Count > 0)
                {
                    result.Add(new ChromeWindow
                    {
                        ProfileName = profileName,
                        ProfileDisplayName = profileDisplayName,
                        WindowIndex = windowIndex++,
                        WindowType = syncWindow.BrowserType,
                        SelectedTabIndex = syncWindow.SelectedTabIndex,
                        Tabs = chromeTabs
                    });
                }
            }
        }

        return result;
    }

    private static DateTime? UnixMillisToDateTime(long millis)
    {
        if (millis <= 0) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }
}
