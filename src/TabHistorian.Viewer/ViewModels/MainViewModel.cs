using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using TabHistorian.Viewer.Data;

namespace TabHistorian.Viewer.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly TabHistorianDb _db;
    private readonly DispatcherTimer _debounceTimer;
    private string _searchText = "";
    private SnapshotInfo? _selectedSnapshot;
    private string _statusText = "";
    private string? _errorMessage;

    public MainViewModel()
    {
        _db = new TabHistorianDb();
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ExecuteSearch();
        };

        LoadSnapshots();
        ExecuteSearch();
    }

    public ObservableCollection<SnapshotInfo> Snapshots { get; } = [];
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

    private void LoadSnapshots()
    {
        var snapshots = _db.GetSnapshots();
        Snapshots.Clear();
        foreach (var s in snapshots)
            Snapshots.Add(s);
    }

    public void ExecuteSearch()
    {
        var query = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var snapshotId = SelectedSnapshot?.Id;

        var rows = _db.SearchTabs(query, snapshotId);
        var tree = BuildTree(rows);

        Results.Clear();
        foreach (var node in tree)
            Results.Add(node);

        int totalTabs = tree.Sum(s => s.Windows.Sum(w => w.Tabs.Count));
        int totalWindows = tree.Sum(s => s.Windows.Count);
        var profiles = tree.SelectMany(s => s.Windows).Select(w => w.ProfileDisplayName).Distinct().Count();

        StatusText = $"{totalTabs} tabs in {totalWindows} windows across {profiles} profiles";

        // Auto-expand when searching
        if (query != null)
        {
            foreach (var snapshot in tree)
            {
                snapshot.IsExpanded = true;
                foreach (var window in snapshot.Windows)
                    window.IsExpanded = true;
            }
        }
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
                TabCount = snapshotGroup.Count()
            };

            foreach (var windowGroup in snapshotGroup.GroupBy(r => r.WindowId))
            {
                var wFirst = windowGroup.First();
                var windowNode = new WindowNode
                {
                    ProfileDisplayName = wFirst.ProfileDisplayName,
                    WindowIndex = wFirst.WindowIndex,
                    TabCount = windowGroup.Count(),
                    WindowTypeLabel = wFirst.WindowType switch { 1 => "popup", 2 => "app", _ => "" },
                    ShowStateLabel = wFirst.ShowState switch { 2 => "minimized", 3 => "maximized", 4 => "fullscreen", _ => "" },
                    IsActive = wFirst.IsActive
                };

                foreach (var tab in windowGroup)
                {
                    var tabNode = new TabNode
                    {
                        Title = tab.Title,
                        CurrentUrl = tab.CurrentUrl,
                        Pinned = tab.Pinned,
                        LastActiveTime = FormatTimestamp(tab.LastActiveTime)
                    };

                    // Parse navigation history JSON
                    try
                    {
                        using var doc = JsonDocument.Parse(tab.NavigationHistory);
                        foreach (var entry in doc.RootElement.EnumerateArray())
                        {
                            tabNode.NavEntries.Add(new NavEntryNode
                            {
                                Url = entry.GetProperty("url").GetString() ?? "",
                                Title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                                Timestamp = entry.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                                    ? FormatTimestamp(ts.GetString()) : null,
                                HttpStatusCode = entry.TryGetProperty("httpStatus", out var hs) && hs.ValueKind == JsonValueKind.Number
                                    ? hs.GetInt32() : 0
                            });
                        }
                    }
                    catch { /* malformed JSON, skip nav history */ }

                    windowNode.Tabs.Add(tabNode);
                }

                snapshotNode.Windows.Add(windowNode);
            }

            snapshots.Add(snapshotNode);
        }

        return snapshots;
    }

    private static string? FormatTimestamp(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        return DateTime.TryParse(iso, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : iso;
    }

    public void ClearSearch()
    {
        SearchText = "";
        SelectedSnapshot = null;
    }

    public void Dispose() => _db.Dispose();
}
