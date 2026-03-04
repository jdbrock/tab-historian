namespace TabHistorian.Models;

public class ChromeWindow
{
    public string ProfileName { get; set; } = string.Empty;
    public string ProfileDisplayName { get; set; } = string.Empty;
    public int WindowIndex { get; set; }
    public int WindowType { get; set; } // 0=normal, 1=popup, 2=app
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ShowState { get; set; } // 0=default, 1=normal, 2=minimized, 3=maximized, 4=fullscreen
    public bool IsActive { get; set; }
    public int SelectedTabIndex { get; set; }
    public string? Workspace { get; set; }
    public string? AppName { get; set; }
    public string? UserTitle { get; set; }
    public List<ChromeTab> Tabs { get; set; } = [];
}
