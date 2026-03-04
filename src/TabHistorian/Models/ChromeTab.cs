namespace TabHistorian.Models;

public class ChromeTab
{
    public int TabIndex { get; set; }
    public string CurrentUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Pinned { get; set; }
    public DateTime? LastActiveTime { get; set; }
    public string? TabGroupToken { get; set; }
    public string? ExtensionAppId { get; set; }
    public List<NavigationEntry> NavigationHistory { get; set; } = [];
}
