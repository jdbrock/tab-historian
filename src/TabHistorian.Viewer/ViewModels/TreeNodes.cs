using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace TabHistorian.Viewer.ViewModels;

public class SnapshotNode : ViewModelBase
{
    private bool _isExpanded;

    public string Timestamp { get; init; } = "";
    public int WindowCount { get; init; }
    public int TabCount { get; init; }
    public int ProfileCount { get; set; }
    public ObservableCollection<ProfileNode> Profiles { get; } = [];

    public string Display => $"\U0001F4F8 {Timestamp}  ({WindowCount} windows, {TabCount} tabs)";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
}

public class ProfileNode : ViewModelBase
{
    private bool _isExpanded;

    public string ProfileDisplayName { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public int TabCount { get; init; }
    public int WindowCount { get; init; }
    public ObservableCollection<WindowNode> Windows { get; } = [];

    public string Display => $"{ProfileDisplayName}  ({WindowCount} windows, {TabCount} tabs)";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
}

public class WindowNode : ViewModelBase
{
    private bool _isExpanded;

    public int WindowIndex { get; init; }
    public int TabCount { get; init; }
    public string WindowTypeLabel { get; init; } = "";
    public string ShowStateLabel { get; init; } = "";
    public bool IsActive { get; init; }
    public string? MostRecentTabTime { get; init; }
    public ObservableCollection<TabNode> Tabs { get; } = [];

    // Detail view properties
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int SelectedTabIndex { get; init; }
    public string? Workspace { get; init; }
    public string? AppName { get; init; }
    public string? UserTitle { get; init; }
    public string ProfileDisplayName { get; init; } = "";
    public string ProfileName { get; init; } = "";

    public string Display
    {
        get
        {
            var parts = $"Window {WindowIndex + 1}  ({TabCount} tabs)";
            if (IsActive) parts += " \u2605";
            if (!string.IsNullOrEmpty(WindowTypeLabel)) parts += $" [{WindowTypeLabel}]";
            if (!string.IsNullOrEmpty(ShowStateLabel)) parts += $" ({ShowStateLabel})";
            if (!string.IsNullOrEmpty(MostRecentTabTime)) parts += $" \u2014 last active {MostRecentTabTime}";
            return parts;
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
}

public class TabNode : ViewModelBase
{
    private bool _isExpanded;
    private BitmapImage? _favicon;

    public string Title { get; init; } = "";
    public string CurrentUrl { get; init; } = "";
    public bool Pinned { get; init; }
    public string? LastActiveTime { get; init; }
    public ObservableCollection<NavEntryNode> NavEntries { get; } = [];

    // Detail view properties
    public int TabIndex { get; init; }
    public string? TabGroupToken { get; init; }
    public string? ExtensionAppId { get; init; }

    public BitmapImage? Favicon
    {
        get => _favicon;
        set => SetField(ref _favicon, value);
    }

    public string Display
    {
        get
        {
            var prefix = Pinned ? "\U0001F4CC " : "";
            var main = string.IsNullOrEmpty(Title) ? CurrentUrl : $"{Title} \u2014 {CurrentUrl}";
            var time = !string.IsNullOrEmpty(LastActiveTime) ? $"  [{LastActiveTime}]" : "";
            return $"{prefix}{main}{time}";
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
}

public class NavEntryNode
{
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Timestamp { get; init; }
    public int HttpStatusCode { get; init; }
    public string? Referrer { get; init; }
    public string? OriginalRequestUrl { get; init; }
    public string? TransitionType { get; init; }
    public bool HasPostData { get; init; }

    public string Display
    {
        get
        {
            var title = string.IsNullOrEmpty(Title) ? "" : $" \u2014 \"{Title}\"";
            var time = !string.IsNullOrEmpty(Timestamp) ? $"  [{Timestamp}]" : "";
            var status = HttpStatusCode > 0 && HttpStatusCode != 200 ? $" ({HttpStatusCode})" : "";
            return $"\u2192 {Url}{title}{status}{time}";
        }
    }
}
