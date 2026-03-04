using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TabHistorian.Viewer.ViewModels;

namespace TabHistorian.Viewer;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TabHistorian Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel?.ClearSearch();
            e.Handled = true;
        }
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearSearch();
        SearchBox.Focus();
    }

    private void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel != null)
            _viewModel.SelectedItem = e.NewValue;

        UpdateDetailPanel(e.NewValue);
    }

    private void UpdateDetailPanel(object? item)
    {
        DetailPanel.Children.Clear();

        switch (item)
        {
            case SnapshotNode snapshot:
                ShowSnapshotDetails(snapshot);
                break;
            case ProfileNode profile:
                ShowProfileDetails(profile);
                break;
            case WindowNode window:
                ShowWindowDetails(window);
                break;
            case TabNode tab:
                ShowTabDetails(tab);
                break;
            case NavEntryNode nav:
                ShowNavEntryDetails(nav);
                break;
            default:
                DetailPanel.Children.Add(new TextBlock
                {
                    Text = "Select an item to view details.",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    FontStyle = FontStyles.Italic
                });
                break;
        }
    }

    private void ShowSnapshotDetails(SnapshotNode snapshot)
    {
        AddHeader("Snapshot Details");
        AddField("Timestamp", snapshot.Timestamp);
        AddField("Windows", snapshot.WindowCount.ToString());
        AddField("Tabs", snapshot.TabCount.ToString());
        AddField("Profiles", snapshot.ProfileCount.ToString());
    }

    private void ShowProfileDetails(ProfileNode profile)
    {
        AddHeader("Profile Details");
        AddField("Display Name", profile.ProfileDisplayName);
        AddField("Profile Name", profile.ProfileName);
        AddField("Windows", profile.WindowCount.ToString());
        AddField("Tabs", profile.TabCount.ToString());
    }

    private void ShowWindowDetails(WindowNode window)
    {
        AddHeader("Window Details");
        AddField("Profile", window.ProfileDisplayName);
        AddField("Profile Name", window.ProfileName);
        AddField("Window Index", window.WindowIndex.ToString());
        AddField("Tab Count", window.TabCount.ToString());
        AddField("Active", window.IsActive ? "Yes" : "No");
        if (!string.IsNullOrEmpty(window.WindowTypeLabel))
            AddField("Type", window.WindowTypeLabel);
        if (!string.IsNullOrEmpty(window.ShowStateLabel))
            AddField("Show State", window.ShowStateLabel);
        if (window.X.HasValue)
            AddField("Position", $"{window.X}, {window.Y}");
        if (window.Width.HasValue)
            AddField("Size", $"{window.Width} x {window.Height}");
        AddField("Selected Tab", window.SelectedTabIndex.ToString());
        if (!string.IsNullOrEmpty(window.MostRecentTabTime))
            AddField("Most Recent Tab", window.MostRecentTabTime);
        if (!string.IsNullOrEmpty(window.Workspace))
            AddField("Workspace", window.Workspace);
        if (!string.IsNullOrEmpty(window.AppName))
            AddField("App Name", window.AppName);
        if (!string.IsNullOrEmpty(window.UserTitle))
            AddField("User Title", window.UserTitle);
    }

    private void ShowTabDetails(TabNode tab)
    {
        AddHeader("Tab Details");

        if (tab.Favicon != null)
        {
            var img = new Image
            {
                Source = tab.Favicon,
                Width = 32, Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6)
            };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
            DetailPanel.Children.Add(img);
        }

        AddField("Title", tab.Title, wrap: true);
        AddField("URL", tab.CurrentUrl, wrap: true);
        AddField("Pinned", tab.Pinned ? "Yes" : "No");
        if (!string.IsNullOrEmpty(tab.LastActiveTime))
            AddField("Last Active", tab.LastActiveTime);
        AddField("Tab Index", tab.TabIndex.ToString());
        if (!string.IsNullOrEmpty(tab.TabGroupToken))
            AddField("Group Token", tab.TabGroupToken);
        if (!string.IsNullOrEmpty(tab.ExtensionAppId))
            AddField("Extension App", tab.ExtensionAppId);
        AddField("History Entries", tab.NavEntries.Count.ToString());

        AddUrlButtons(tab.CurrentUrl);
    }

    private void ShowNavEntryDetails(NavEntryNode nav)
    {
        AddHeader("Navigation Entry");
        AddField("URL", nav.Url, wrap: true);
        AddField("Title", nav.Title, wrap: true);
        if (!string.IsNullOrEmpty(nav.Timestamp))
            AddField("Timestamp", nav.Timestamp);
        if (nav.HttpStatusCode > 0)
            AddField("HTTP Status", nav.HttpStatusCode.ToString());
        if (!string.IsNullOrEmpty(nav.Referrer))
            AddField("Referrer", nav.Referrer, wrap: true);
        if (!string.IsNullOrEmpty(nav.OriginalRequestUrl))
            AddField("Original URL", nav.OriginalRequestUrl, wrap: true);
        if (!string.IsNullOrEmpty(nav.TransitionType))
            AddField("Transition", nav.TransitionType);
        if (nav.HasPostData)
            AddField("Has POST Data", "Yes");

        AddUrlButtons(nav.Url);
    }

    private void AddHeader(string text)
    {
        DetailPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    private void AddField(string label, string? value, bool wrap = false)
    {
        if (string.IsNullOrEmpty(value)) return;

        var tb = new TextBlock
        {
            Margin = new Thickness(0, 1, 0, 1),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        tb.Inlines.Add(new Run(label + ": ") { FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new Run(value));
        DetailPanel.Children.Add(tb);
    }

    private void AddUrlButtons(string? url)
    {
        if (string.IsNullOrEmpty(url) || (!url.StartsWith("http://") && !url.StartsWith("https://")))
            return;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var chromeBtn = new Button { Content = "Open in Chrome", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0) };
        chromeBtn.Click += (_, _) => OpenUrl(url, "chrome");
        panel.Children.Add(chromeBtn);

        var edgeBtn = new Button { Content = "Open in Edge", Padding = new Thickness(8, 4, 8, 4) };
        edgeBtn.Click += (_, _) => OpenUrl(url, "msedge");
        panel.Children.Add(edgeBtn);

        DetailPanel.Children.Add(panel);
    }

    private static void OpenUrl(string? url, string browser)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(browser, url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {browser}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }
}
