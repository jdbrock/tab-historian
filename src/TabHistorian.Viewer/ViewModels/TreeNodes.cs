using System.Collections.ObjectModel;

namespace TabHistorian.Viewer.ViewModels;

public class SnapshotNode : ViewModelBase
{
    private bool _isExpanded;

    public string Timestamp { get; init; } = "";
    public int WindowCount { get; init; }
    public int TabCount { get; init; }
    public ObservableCollection<WindowNode> Windows { get; } = [];

    public string Display => $"\U0001F4F8 {Timestamp}  ({WindowCount} windows, {TabCount} tabs)";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
}

public class WindowNode : ViewModelBase
{
    private bool _isExpanded;

    public string ProfileDisplayName { get; init; } = "";
    public int WindowIndex { get; init; }
    public int TabCount { get; init; }
    public string WindowTypeLabel { get; init; } = "";
    public string ShowStateLabel { get; init; } = "";
    public bool IsActive { get; init; }
    public ObservableCollection<TabNode> Tabs { get; } = [];

    public string Display
    {
        get
        {
            var parts = $"{ProfileDisplayName} \u2014 Window {WindowIndex + 1}  ({TabCount} tabs)";
            if (IsActive) parts += " \u2605"; // star for active window
            if (!string.IsNullOrEmpty(WindowTypeLabel)) parts += $" [{WindowTypeLabel}]";
            if (!string.IsNullOrEmpty(ShowStateLabel)) parts += $" ({ShowStateLabel})";
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

    public string Title { get; init; } = "";
    public string CurrentUrl { get; init; } = "";
    public bool Pinned { get; init; }
    public string? LastActiveTime { get; init; }
    public ObservableCollection<NavEntryNode> NavEntries { get; } = [];

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
