namespace TabHistorian.Models;

public class NavigationEntry
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int TransitionType { get; set; }
    public DateTime? Timestamp { get; set; }
    public string ReferrerUrl { get; set; } = string.Empty;
    public string OriginalRequestUrl { get; set; } = string.Empty;
    public int HttpStatusCode { get; set; }
    public bool HasPostData { get; set; }
}
