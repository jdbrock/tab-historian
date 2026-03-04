using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TabHistorian.Viewer.Data;
using TabHistorian.Viewer.Services;

namespace TabHistorian.Viewer.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly TabHistorianDb _db;
    private readonly DispatcherTimer _debounceTimer;
    private readonly FileSystemWatcher _dbWatcher;
    private readonly DispatcherTimer _refreshDebounce;
    private string _searchText = "";
    private SnapshotInfo? _selectedSnapshot;
    private ProfileInfo? _selectedProfile;
    private string _statusText = "";
    private string? _errorMessage;
    private object? _selectedItem;
    private int _lastSnapshotCount;

    public MainViewModel()
    {
        _db = new TabHistorianDb();
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ExecuteSearch();
        };

        // Watch for database changes (new snapshots from the service)
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            RefreshIfNewSnapshots();
        };

        _dbWatcher = new FileSystemWatcher(Path.GetDirectoryName(_db.DbPath)!, Path.GetFileName(_db.DbPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _dbWatcher.Changed += (_, _) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _refreshDebounce.Stop();
                _refreshDebounce.Start();
            });
        };
        _dbWatcher.EnableRaisingEvents = true;

        LoadSnapshots();
        LoadProfiles();
        _lastSnapshotCount = Snapshots.Count;
        ExecuteSearch();
    }

    public ObservableCollection<SnapshotInfo> Snapshots { get; } = [];
    public ObservableCollection<ProfileInfo> Profiles { get; } = [];
    public ObservableCollection<SnapshotNode> Results { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
        }
    }

    public SnapshotInfo? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (SetField(ref _selectedSnapshot, value))
                ExecuteSearch();
        }
    }

    public ProfileInfo? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
                ExecuteSearch();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetField(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(SelectedUrl));
                OnPropertyChanged(nameof(HasSelectedUrl));
            }
        }
    }

    public string? SelectedUrl => SelectedItem switch
    {
        TabNode tab => tab.CurrentUrl,
        NavEntryNode nav => nav.Url,
        _ => null
    };

    public bool HasSelectedUrl => !string.IsNullOrEmpty(SelectedUrl);

    private void LoadSnapshots()
    {
        var snapshots = _db.GetSnapshots();
        Snapshots.Clear();
        foreach (var s in snapshots)
            Snapshots.Add(s);
    }

    private void LoadProfiles()
    {
        var profiles = _db.GetProfiles();
        Profiles.Clear();
        foreach (var p in profiles)
            Profiles.Add(p);
    }

    public void ExecuteSearch()
    {
        var query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var snapshotId = SelectedSnapshot?.Id;
        var profileName = SelectedProfile?.ProfileName;

        var rows = _db.SearchTabs(query, snapshotId, profileName);
        var tree = BuildTree(rows);

        Results.Clear();
        foreach (var node in tree)
            Results.Add(node);

        int totalTabs = tree.Sum(s => s.Profiles.Sum(p => p.Windows.Sum(w => w.Tabs.Count)));
        int totalWindows = tree.Sum(s => s.Profiles.Sum(p => p.Windows.Count));
        var profiles = tree.Sum(s => s.Profiles.Count);

        StatusText = $"{totalTabs} tabs in {totalWindows} windows across {profiles} profiles";
    }

    private static List<SnapshotNode> BuildTree(List<TabRow> rows)
    {
        var snapshots = new List<SnapshotNode>();

        foreach (var snapshotGroup in rows.GroupBy(r => r.SnapshotId))
        {
            var first = snapshotGroup.First();
            var snapshotNode = new SnapshotNode
            {
                Timestamp = FormatTimestamp(first.SnapshotTimestamp),
                WindowCount = snapshotGroup.Select(r => r.WindowId).Distinct().Count(),
                TabCount = snapshotGroup.Count(),
                IsExpanded = true
            };

            // Group by profile
            foreach (var profileGroup in snapshotGroup.GroupBy(r => r.ProfileName))
            {
                var pFirst = profileGroup.First();
                var profileNode = new ProfileNode
                {
                    ProfileName = pFirst.ProfileName,
                    ProfileDisplayName = pFirst.ProfileDisplayName,
                    WindowCount = profileGroup.Select(r => r.WindowId).Distinct().Count(),
                    TabCount = profileGroup.Count(),
                    IsExpanded = true
                };

                foreach (var windowGroup in profileGroup.GroupBy(r => r.WindowId))
                {
                    var wFirst = windowGroup.First();
                    var tabNodes = new List<TabNode>();

                    foreach (var tab in windowGroup)
                    {
                        var tabNode = new TabNode
                        {
                            Title = tab.Title,
                            CurrentUrl = tab.CurrentUrl,
                            Pinned = tab.Pinned,
                            LastActiveTime = FormatTimestamp(tab.LastActiveTime),
                            TabIndex = tab.TabIndex,
                            TabGroupToken = tab.TabGroupToken,
                            ExtensionAppId = tab.ExtensionAppId
                        };

                        // Parse navigation history JSON
                        var navEntries = new List<NavEntryNode>();
                        try
                        {
                            using var doc = JsonDocument.Parse(tab.NavigationHistory);
                            foreach (var entry in doc.RootElement.EnumerateArray())
                            {
                                navEntries.Add(new NavEntryNode
                                {
                                    Url = entry.GetProperty("url").GetString() ?? "",
                                    Title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                                    Timestamp = entry.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                                        ? FormatTimestamp(ts.GetString()) : null,
                                    HttpStatusCode = entry.TryGetProperty("httpStatus", out var hs) && hs.ValueKind == JsonValueKind.Number
                                        ? hs.GetInt32() : 0,
                                    Referrer = entry.TryGetProperty("referrer", out var r) ? r.GetString() : null,
                                    OriginalRequestUrl = entry.TryGetProperty("originalRequestUrl", out var oru) ? oru.GetString() : null,
                                    TransitionType = entry.TryGetProperty("transitionType", out var tt) ? tt.GetString() : null,
                                    HasPostData = entry.TryGetProperty("hasPostData", out var hp) && hp.ValueKind == JsonValueKind.True
                                });
                            }
                        }
                        catch { /* malformed JSON, skip nav history */ }

                        // Sort nav entries by timestamp DESC
                        foreach (var nav in navEntries.OrderByDescending(n => n.Timestamp ?? ""))
                            tabNode.NavEntries.Add(nav);

                        tabNodes.Add(tabNode);
                    }

                    // Sort tabs by LastActiveTime DESC
                    tabNodes.Sort((a, b) => string.Compare(b.LastActiveTime ?? "", a.LastActiveTime ?? "", StringComparison.Ordinal));

                    var mostRecentTab = tabNodes.FirstOrDefault(t => t.LastActiveTime != null);

                    var windowNode = new WindowNode
                    {
                        ProfileDisplayName = wFirst.ProfileDisplayName,
                        ProfileName = wFirst.ProfileName,
                        WindowIndex = wFirst.WindowIndex,
                        TabCount = windowGroup.Count(),
                        WindowTypeLabel = wFirst.WindowType switch { 1 => "popup", 2 => "app", _ => "" },
                        ShowStateLabel = wFirst.ShowState switch { 2 => "minimized", 3 => "maximized", 4 => "fullscreen", _ => "" },
                        IsActive = wFirst.IsActive,
                        MostRecentTabTime = mostRecentTab?.LastActiveTime,
                        X = wFirst.X,
                        Y = wFirst.Y,
                        Width = wFirst.Width,
                        Height = wFirst.Height,
                        SelectedTabIndex = wFirst.SelectedTabIndex,
                        Workspace = wFirst.Workspace,
                        AppName = wFirst.AppName,
                        UserTitle = wFirst.UserTitle,
                        IsExpanded = true
                    };

                    foreach (var tab in tabNodes)
                        windowNode.Tabs.Add(tab);

                    profileNode.Windows.Add(windowNode);
                }

                // Sort windows within profile by most recent tab DESC
                var sortedWindows = profileNode.Windows
                    .OrderByDescending(w => w.MostRecentTabTime ?? "")
                    .ToList();
                profileNode.Windows.Clear();
                foreach (var w in sortedWindows)
                    profileNode.Windows.Add(w);

                snapshotNode.Profiles.Add(profileNode);
            }

            // Sort profiles by most recent tab DESC
            var sortedProfiles = snapshotNode.Profiles
                .OrderByDescending(p => p.Windows.SelectMany(w => w.Tabs).Max(t => t.LastActiveTime ?? ""))
                .ToList();
            snapshotNode.Profiles.Clear();
            foreach (var p in sortedProfiles)
                snapshotNode.Profiles.Add(p);

            snapshotNode.ProfileCount = snapshotNode.Profiles.Count;

            snapshots.Add(snapshotNode);
        }

        // Load favicons asynchronously
        _ = LoadFaviconsAsync(snapshots);

        return snapshots;
    }

    private static async Task LoadFaviconsAsync(List<SnapshotNode> snapshots)
    {
        var tabsByDomain = new Dictionary<string, List<TabNode>>();

        foreach (var snapshot in snapshots)
            foreach (var profile in snapshot.Profiles)
                foreach (var window in profile.Windows)
                    foreach (var tab in window.Tabs)
                    {
                        var domain = FaviconService.ExtractDomain(tab.CurrentUrl);
                        if (domain == null) continue;
                        if (!tabsByDomain.ContainsKey(domain))
                            tabsByDomain[domain] = [];
                        tabsByDomain[domain].Add(tab);
                    }

        // Fetch favicons in parallel, batched
        var tasks = tabsByDomain.Select(async kvp =>
        {
            var favicon = await FaviconService.GetFaviconAsync(kvp.Key);
            if (favicon != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var tab in kvp.Value)
                        tab.Favicon = favicon;
                });
            }
        });

        await Task.WhenAll(tasks);
    }

    private static string? FormatTimestamp(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTime.TryParse(iso, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : iso;
    }

    private void RefreshIfNewSnapshots()
    {
        try
        {
            var snapshots = _db.GetSnapshots();
            if (snapshots.Count != _lastSnapshotCount)
            {
                _lastSnapshotCount = snapshots.Count;
                Snapshots.Clear();
                foreach (var s in snapshots)
                    Snapshots.Add(s);
                LoadProfiles();

                // Auto-select latest snapshot if no specific snapshot was chosen
                if (SelectedSnapshot == null && Snapshots.Count > 0)
                {
                    SelectedSnapshot = Snapshots[0];
                }
                else
                {
                    ExecuteSearch();
                }
            }
        }
        catch { /* DB may be briefly locked during write */ }
    }

    public void ClearSearch()
    {
        SearchText = "";
        SelectedSnapshot = null;
        SelectedProfile = null;
    }

    public void Dispose()
    {
        _dbWatcher.Dispose();
        _db.Dispose();
    }
}
