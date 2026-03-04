using TabHistorian.Models;
using TabHistorian.Parsing;

namespace TabHistorian.Services;

/// <summary>
/// Reads and reconstructs Chrome session state from SNSS files.
/// Copies files to temp before reading (Chrome holds locks on originals).
/// ALL access to Chrome files is strictly READ-ONLY.
/// </summary>
public class SessionFileReader
{
    // Session file command IDs (from Chromium source: session_service_commands.cc)
    private const byte CmdSetTabWindow = 0;
    private const byte CmdSetTabIndexInWindow = 2;
    private const byte CmdUpdateTabNavigation = 6;
    private const byte CmdSetSelectedNavigationIndex = 7;
    private const byte CmdSetSelectedTabInIndex = 8;
    private const byte CmdSetWindowType = 9;
    private const byte CmdSetPinnedState = 12;
    private const byte CmdSetExtensionAppId = 13;
    private const byte CmdSetWindowBounds3 = 14;
    private const byte CmdSetWindowAppName = 15;
    private const byte CmdTabClosed = 16;
    private const byte CmdWindowClosed = 17;
    private const byte CmdSetActiveWindow = 20;
    private const byte CmdLastActiveTime = 21;
    private const byte CmdSetWindowWorkspace2 = 23;
    private const byte CmdSetTabGroup = 25;
    private const byte CmdSetWindowUserTitle = 31;
    private const byte CmdSetWindowVisibleOnAllWorkspaces = 32;

    // WebKit epoch: 1601-01-01 00:00:00 UTC (same as Windows FILETIME but in microseconds)
    private static readonly DateTime WebKitEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ILogger<SessionFileReader> _logger;
    private readonly VssShadowCopy _vss;
    private readonly SnssParser _parser = new();

    public SessionFileReader(ILogger<SessionFileReader> logger, VssShadowCopy vss)
    {
        _logger = logger;
        _vss = vss;
    }

    public List<ChromeWindow> ReadProfile(ChromeProfile profile)
    {
        var sessionsDir = Path.Combine(profile.FullPath, "Sessions");
        if (!Directory.Exists(sessionsDir))
        {
            _logger.LogDebug("No Sessions directory for profile {Profile}", profile.DisplayName);
            return [];
        }

        // Get session files ordered newest first. When Chrome closes cleanly,
        // the newest file may be empty — fall back to the second newest.
        var sessionFiles = Directory.GetFiles(sessionsDir, "Session_*")
            .OrderByDescending(f => f)
            .ToList();

        if (sessionFiles.Count == 0)
        {
            _logger.LogDebug("No session files found for profile {Profile}", profile.DisplayName);
            return [];
        }

        foreach (var sessionFile in sessionFiles)
        {
            _logger.LogDebug("Trying session file: {File} ({Size} bytes)",
                sessionFile, new FileInfo(sessionFile).Length);

            string tempFile = Path.Combine(Path.GetTempPath(), $"tabhistorian_{Guid.NewGuid()}.snss");
            try
            {
                // Copy the session file to temp. Chrome holds exclusive locks on current
                // session files. Try normal file copy first, fall back to VSS shadow copy.
                if (!TryCopyFile(sessionFile, tempFile))
                    continue;

                var windows = ParseSessionFile(tempFile, profile);
                if (windows.Count > 0)
                    return windows;

                _logger.LogDebug("No windows found in {File}, trying next file", sessionFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading session file {File}", sessionFile);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort cleanup */ }
            }
        }

        _logger.LogDebug("No usable session files for profile {Profile}", profile.DisplayName);
        return [];
    }

    /// <summary>
    /// Creates a VSS shadow copy for reading locked files. Call before reading profiles,
    /// and dispose/delete after all profiles are read.
    /// </summary>
    public bool EnsureVssSnapshot(string chromeUserDataPath)
    {
        if (_vss.DevicePath != null)
            return true; // already have one

        if (_vss.Create(chromeUserDataPath))
        {
            _logger.LogInformation("Created VSS shadow copy for reading locked Chrome files");
            return true;
        }

        _logger.LogWarning("Could not create VSS shadow copy — locked files will be skipped. Run elevated for full access.");
        return false;
    }

    /// <summary>
    /// Releases the VSS shadow copy.
    /// </summary>
    public void ReleaseVssSnapshot()
    {
        _vss.Delete();
    }

    private bool TryCopyFile(string source, string dest)
    {
        // Try normal file copy first
        try
        {
            using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write);
            srcStream.CopyTo(dstStream);
            return true;
        }
        catch (IOException)
        {
            // File is exclusively locked — try VSS
        }

        // Fall back to VSS shadow copy
        if (_vss.DevicePath != null && _vss.CopyFile(source, dest))
        {
            _logger.LogDebug("Read locked file via VSS: {File}", source);
            return true;
        }

        _logger.LogWarning("Cannot read locked file (no VSS available): {File}", source);
        return false;
    }

    private List<ChromeWindow> ParseSessionFile(string filePath, ChromeProfile profile)
    {
        List<SnssCommand> commands;
        using (var stream = File.OpenRead(filePath))
        {
            commands = _parser.Parse(stream);
        }

        _logger.LogDebug("Parsed {Count} commands from session file", commands.Count);

        // State dictionaries keyed by session IDs
        var windows = new Dictionary<int, WindowState>();
        var tabs = new Dictionary<int, TabState>();
        int activeWindowId = -1;

        foreach (var cmd in commands)
        {
            try
            {
                ProcessCommand(cmd, windows, tabs, ref activeWindowId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Skipping command {Id}: {Error}", cmd.Id, ex.Message);
            }
        }

        _logger.LogDebug("Replayed into {Windows} windows, {Tabs} tabs",
            windows.Count(w => !w.Value.Closed), tabs.Count(t => !t.Value.Closed));

        // Build result: group tabs by window, filter out closed entities
        return AssembleWindows(windows, tabs, profile, activeWindowId);
    }

    private void ProcessCommand(SnssCommand cmd, Dictionary<int, WindowState> windows,
        Dictionary<int, TabState> tabs, ref int activeWindowId)
    {
        switch (cmd.Id)
        {
            case CmdSetTabWindow:
            {
                // Payload is (windowId, tabId) — window ID comes first
                var (windowId, tabId) = ReadIdAndIndex(cmd.Payload);
                GetOrCreateTab(tabs, tabId).WindowId = windowId;
                GetOrCreateWindow(windows, windowId);
                break;
            }
            case CmdSetTabIndexInWindow:
            {
                var (tabId, index) = ReadIdAndIndex(cmd.Payload);
                GetOrCreateTab(tabs, tabId).TabIndex = index;
                break;
            }
            case CmdUpdateTabNavigation:
            {
                ParseNavigationEntry(cmd.Payload, tabs);
                break;
            }
            case CmdSetSelectedNavigationIndex:
            {
                var (tabId, index) = ReadIdAndIndex(cmd.Payload);
                GetOrCreateTab(tabs, tabId).SelectedNavIndex = index;
                break;
            }
            case CmdSetSelectedTabInIndex:
            {
                var (windowId, index) = ReadIdAndIndex(cmd.Payload);
                GetOrCreateWindow(windows, windowId).SelectedTabIndex = index;
                break;
            }
            case CmdSetWindowType:
            {
                var (windowId, windowType) = ReadIdAndIndex(cmd.Payload);
                GetOrCreateWindow(windows, windowId).WindowType = windowType;
                break;
            }
            case CmdSetPinnedState:
            {
                if (cmd.Payload.Length >= 5)
                {
                    int tabId = BitConverter.ToInt32(cmd.Payload, 0);
                    bool pinned = cmd.Payload[4] != 0;
                    GetOrCreateTab(tabs, tabId).Pinned = pinned;
                }
                break;
            }
            case CmdSetExtensionAppId:
            {
                var pickle = new PickleReader(cmd.Payload);
                int tabId = pickle.ReadInt32();
                string appId = pickle.ReadString();
                GetOrCreateTab(tabs, tabId).ExtensionAppId = appId;
                break;
            }
            case CmdSetWindowBounds3:
            {
                if (cmd.Payload.Length >= 24)
                {
                    int windowId = BitConverter.ToInt32(cmd.Payload, 0);
                    var win = GetOrCreateWindow(windows, windowId);
                    win.X = BitConverter.ToInt32(cmd.Payload, 4);
                    win.Y = BitConverter.ToInt32(cmd.Payload, 8);
                    win.Width = BitConverter.ToInt32(cmd.Payload, 12);
                    win.Height = BitConverter.ToInt32(cmd.Payload, 16);
                    win.ShowState = BitConverter.ToInt32(cmd.Payload, 20);
                }
                break;
            }
            case CmdSetWindowAppName:
            {
                var pickle = new PickleReader(cmd.Payload);
                int windowId = pickle.ReadInt32();
                string appName = pickle.ReadString();
                GetOrCreateWindow(windows, windowId).AppName = appName;
                break;
            }
            case CmdTabClosed:
            {
                if (cmd.Payload.Length >= 4)
                {
                    int tabId = BitConverter.ToInt32(cmd.Payload, 0);
                    if (tabs.ContainsKey(tabId))
                        tabs[tabId].Closed = true;
                }
                break;
            }
            case CmdWindowClosed:
            {
                if (cmd.Payload.Length >= 4)
                {
                    int windowId = BitConverter.ToInt32(cmd.Payload, 0);
                    if (windows.ContainsKey(windowId))
                        windows[windowId].Closed = true;
                }
                break;
            }
            case CmdSetActiveWindow:
            {
                var (windowId, _) = ReadIdAndIndex(cmd.Payload);
                activeWindowId = windowId;
                break;
            }
            case CmdLastActiveTime:
            {
                // IdAndPayload64 struct: int32 id (offset 0), 4 bytes padding, int64 payload (offset 8)
                if (cmd.Payload.Length >= 16)
                {
                    int tabId = BitConverter.ToInt32(cmd.Payload, 0);
                    long timestamp = BitConverter.ToInt64(cmd.Payload, 8);
                    GetOrCreateTab(tabs, tabId).LastActiveTime = WebKitToDateTime(timestamp);
                }
                break;
            }
            case CmdSetWindowWorkspace2:
            {
                var pickle = new PickleReader(cmd.Payload);
                int windowId = pickle.ReadInt32();
                string workspace = pickle.ReadString();
                GetOrCreateWindow(windows, windowId).Workspace = workspace;
                break;
            }
            case CmdSetTabGroup:
            {
                if (cmd.Payload.Length >= 21) // int32 + uint64 + uint64 + bool
                {
                    int tabId = BitConverter.ToInt32(cmd.Payload, 0);
                    ulong tokenHigh = BitConverter.ToUInt64(cmd.Payload, 4);
                    ulong tokenLow = BitConverter.ToUInt64(cmd.Payload, 12);
                    bool hasGroup = cmd.Payload[20] != 0;
                    var tab = GetOrCreateTab(tabs, tabId);
                    tab.TabGroupToken = hasGroup ? $"{tokenHigh:X16}{tokenLow:X16}" : null;
                }
                break;
            }
            case CmdSetWindowUserTitle:
            {
                var pickle = new PickleReader(cmd.Payload);
                int windowId = pickle.ReadInt32();
                string title = pickle.ReadString();
                GetOrCreateWindow(windows, windowId).UserTitle = title;
                break;
            }
            case CmdSetWindowVisibleOnAllWorkspaces:
            {
                if (cmd.Payload.Length >= 5)
                {
                    int windowId = BitConverter.ToInt32(cmd.Payload, 0);
                    bool visible = cmd.Payload[4] != 0;
                    GetOrCreateWindow(windows, windowId).VisibleOnAllWorkspaces = visible;
                }
                break;
            }
        }
    }

    private void ParseNavigationEntry(byte[] payload, Dictionary<int, TabState> tabs)
    {
        var pickle = new PickleReader(payload);

        int tabId = pickle.ReadInt32();
        int navIndex = pickle.ReadInt32();
        string url = pickle.ReadString();
        string title = pickle.ReadString16();

        // Skip encoded_page_state (can be large)
        pickle.ReadString();

        int transitionType = pickle.ReadInt32();
        int typeMask = pickle.ReadInt32();
        bool hasPostData = (typeMask & 1) != 0;

        string referrerUrl = pickle.ReadString();
        pickle.ReadInt32(); // obsolete_referrer_policy (always 0)
        string originalRequestUrl = pickle.ReadString();
        pickle.ReadBool(); // is_overriding_user_agent

        long timestamp = pickle.ReadInt64();

        pickle.ReadString16(); // search_terms (always empty)
        int httpStatusCode = pickle.ReadInt32();

        var tab = GetOrCreateTab(tabs, tabId);

        // Ensure nav list is big enough
        while (tab.NavigationEntries.Count <= navIndex)
            tab.NavigationEntries.Add(new NavigationEntry());

        tab.NavigationEntries[navIndex] = new NavigationEntry
        {
            Url = url,
            Title = title,
            TransitionType = transitionType & 0xFF,
            Timestamp = WebKitToDateTime(timestamp),
            ReferrerUrl = referrerUrl,
            OriginalRequestUrl = originalRequestUrl,
            HttpStatusCode = httpStatusCode,
            HasPostData = hasPostData
        };
    }

    private static DateTime? WebKitToDateTime(long webkitTimestamp)
    {
        if (webkitTimestamp <= 0) return null;
        try
        {
            return WebKitEpoch.AddTicks(webkitTimestamp * 10);
        }
        catch
        {
            return null;
        }
    }

    private static (int id, int index) ReadIdAndIndex(byte[] payload)
    {
        if (payload.Length < 8)
            throw new InvalidDataException("IDAndIndex payload too short");

        return (BitConverter.ToInt32(payload, 0), BitConverter.ToInt32(payload, 4));
    }

    private List<ChromeWindow> AssembleWindows(
        Dictionary<int, WindowState> windows,
        Dictionary<int, TabState> tabs,
        ChromeProfile profile,
        int activeWindowId)
    {
        var result = new List<ChromeWindow>();
        int windowIndex = 0;

        foreach (var (windowId, windowState) in windows.Where(w => !w.Value.Closed))
        {
            var windowTabs = tabs
                .Where(t => !t.Value.Closed && t.Value.WindowId == windowId)
                .OrderBy(t => t.Value.TabIndex)
                .Select(t =>
                {
                    var tabState = t.Value;
                    int selectedIdx = tabState.SelectedNavIndex;
                    var navEntries = tabState.NavigationEntries
                        .Where(n => !string.IsNullOrEmpty(n.Url))
                        .ToList();

                    string currentUrl = "";
                    string currentTitle = "";
                    if (selectedIdx >= 0 && selectedIdx < tabState.NavigationEntries.Count)
                    {
                        currentUrl = tabState.NavigationEntries[selectedIdx].Url;
                        currentTitle = tabState.NavigationEntries[selectedIdx].Title;
                    }
                    else if (navEntries.Count > 0)
                    {
                        currentUrl = navEntries[^1].Url;
                        currentTitle = navEntries[^1].Title;
                    }

                    return new ChromeTab
                    {
                        TabIndex = tabState.TabIndex,
                        CurrentUrl = currentUrl,
                        Title = currentTitle,
                        Pinned = tabState.Pinned,
                        LastActiveTime = tabState.LastActiveTime,
                        TabGroupToken = tabState.TabGroupToken,
                        ExtensionAppId = tabState.ExtensionAppId,
                        NavigationHistory = navEntries
                    };
                })
                .Where(t => !string.IsNullOrEmpty(t.CurrentUrl))
                .ToList();

            if (windowTabs.Count > 0)
            {
                result.Add(new ChromeWindow
                {
                    ProfileName = profile.DirectoryName,
                    ProfileDisplayName = profile.DisplayName,
                    WindowIndex = windowIndex++,
                    WindowType = windowState.WindowType,
                    X = windowState.X,
                    Y = windowState.Y,
                    Width = windowState.Width,
                    Height = windowState.Height,
                    ShowState = windowState.ShowState,
                    IsActive = windowId == activeWindowId,
                    SelectedTabIndex = windowState.SelectedTabIndex,
                    Workspace = windowState.Workspace,
                    AppName = windowState.AppName,
                    UserTitle = windowState.UserTitle,
                    Tabs = windowTabs
                });
            }
        }

        return result;
    }

    private static WindowState GetOrCreateWindow(Dictionary<int, WindowState> windows, int id)
    {
        if (!windows.TryGetValue(id, out var state))
        {
            state = new WindowState();
            windows[id] = state;
        }
        return state;
    }

    private static TabState GetOrCreateTab(Dictionary<int, TabState> tabs, int id)
    {
        if (!tabs.TryGetValue(id, out var state))
        {
            state = new TabState();
            tabs[id] = state;
        }
        return state;
    }

    private class WindowState
    {
        public int SelectedTabIndex { get; set; }
        public int WindowType { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ShowState { get; set; }
        public string? Workspace { get; set; }
        public string? AppName { get; set; }
        public string? UserTitle { get; set; }
        public bool VisibleOnAllWorkspaces { get; set; }
        public bool Closed { get; set; }
    }

    private class TabState
    {
        public int WindowId { get; set; }
        public int TabIndex { get; set; }
        public int SelectedNavIndex { get; set; } = -1;
        public bool Pinned { get; set; }
        public bool Closed { get; set; }
        public DateTime? LastActiveTime { get; set; }
        public string? TabGroupToken { get; set; }
        public string? ExtensionAppId { get; set; }
        public List<NavigationEntry> NavigationEntries { get; set; } = [];
    }
}
