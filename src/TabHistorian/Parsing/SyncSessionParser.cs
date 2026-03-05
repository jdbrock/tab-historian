namespace TabHistorian.Parsing;

/// <summary>
/// Decodes Chrome sync SessionSpecifics protobufs into structured records.
/// Field numbers match components/sync/protocol/session_specifics.proto.
/// </summary>
public class SyncSessionParser
{
    // SessionSpecifics fields
    private const int FieldSessionTag = 1;
    private const int FieldHeader = 2;
    private const int FieldTab = 3;
    private const int FieldTabNodeId = 4;

    // SessionHeader fields
    private const int FieldHeaderWindow = 2;
    private const int FieldHeaderClientName = 3;

    // SessionWindow fields
    private const int FieldWindowId = 1;
    private const int FieldWindowSelectedTabIndex = 2;
    private const int FieldWindowBrowserType = 3;
    private const int FieldWindowTabNodeId = 4;

    // SessionTab fields
    private const int FieldTabTabId = 1;
    private const int FieldTabWindowId = 2;
    private const int FieldTabVisualIndex = 3;
    private const int FieldTabCurrentNavIndex = 4;
    private const int FieldTabPinned = 5;
    private const int FieldTabExtensionAppId = 6;
    private const int FieldTabNavigation = 7;
    private const int FieldTabLastActiveTime = 14;

    // TabNavigation fields
    private const int FieldNavUrl = 2;
    private const int FieldNavReferrer = 3;
    private const int FieldNavTitle = 4;
    private const int FieldNavPageTransition = 6;
    private const int FieldNavTimestamp = 9;
    private const int FieldNavHttpStatusCode = 15;

    public record ParsedSpecifics(
        string SessionTag,
        ParsedHeader? Header,
        ParsedTab? Tab,
        int TabNodeId);

    public record ParsedHeader(string ClientName, List<ParsedWindow> Windows);

    public record ParsedWindow(
        int WindowId,
        int SelectedTabIndex,
        int BrowserType,
        List<int> TabNodeIds);

    public record ParsedTab(
        int TabId,
        int WindowId,
        int TabVisualIndex,
        int CurrentNavigationIndex,
        bool Pinned,
        string? ExtensionAppId,
        long LastActiveTimeMillis,
        List<ParsedNavigation> Navigations);

    public record ParsedNavigation(
        string Url,
        string Title,
        string Referrer,
        int PageTransition,
        long TimestampMsec,
        int HttpStatusCode);

    /// <summary>
    /// Attempts to parse a byte array as a SessionSpecifics proto.
    /// Returns null if parsing fails (e.g., not a valid SessionSpecifics).
    /// </summary>
    public (ParsedSpecifics? Result, string? Error) TryParseWithError(byte[] data)
    {
        try
        {
            var result = ParseSessionSpecifics(new ProtobufReader(data));
            if (string.IsNullOrEmpty(result.SessionTag))
                return (null, "empty session_tag");
            return (result, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    public ParsedSpecifics? TryParse(byte[] data)
        => TryParseWithError(data).Result;

    private static ParsedSpecifics ParseSessionSpecifics(ProtobufReader reader)
    {
        string sessionTag = "";
        ParsedHeader? header = null;
        ParsedTab? tab = null;
        int tabNodeId = 0;

        while (reader.HasData)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case FieldSessionTag when wire == 2:
                    sessionTag = reader.ReadString();
                    break;
                case FieldHeader when wire == 2:
                    header = ParseHeader(reader.ReadMessage());
                    break;
                case FieldTab when wire == 2:
                    tab = ParseTab(reader.ReadMessage());
                    break;
                case FieldTabNodeId when wire == 0:
                    tabNodeId = reader.ReadInt32();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParsedSpecifics(sessionTag, header, tab, tabNodeId);
    }

    private static ParsedHeader ParseHeader(ProtobufReader reader)
    {
        string clientName = "";
        var windows = new List<ParsedWindow>();

        while (reader.HasData)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case FieldHeaderClientName when wire == 2:
                    clientName = reader.ReadString();
                    break;
                case FieldHeaderWindow when wire == 2:
                    windows.Add(ParseWindow(reader.ReadMessage()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParsedHeader(clientName, windows);
    }

    private static ParsedWindow ParseWindow(ProtobufReader reader)
    {
        int windowId = 0, selectedTabIndex = 0, browserType = 0;
        var tabNodeIds = new List<int>();

        while (reader.HasData)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case FieldWindowId when wire == 0:
                    windowId = reader.ReadInt32();
                    break;
                case FieldWindowSelectedTabIndex when wire == 0:
                    selectedTabIndex = reader.ReadInt32();
                    break;
                case FieldWindowBrowserType when wire == 0:
                    browserType = reader.ReadInt32();
                    break;
                case FieldWindowTabNodeId when wire == 0:
                    tabNodeIds.Add(reader.ReadInt32());
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParsedWindow(windowId, selectedTabIndex, browserType, tabNodeIds);
    }

    private static ParsedTab ParseTab(ProtobufReader reader)
    {
        int tabId = 0, windowId = 0, tabVisualIndex = 0, currentNavIndex = 0;
        bool pinned = false;
        string? extensionAppId = null;
        long lastActiveTime = 0;
        var navigations = new List<ParsedNavigation>();

        while (reader.HasData)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case FieldTabTabId when wire == 0:
                    tabId = reader.ReadInt32();
                    break;
                case FieldTabWindowId when wire == 0:
                    windowId = reader.ReadInt32();
                    break;
                case FieldTabVisualIndex when wire == 0:
                    tabVisualIndex = reader.ReadInt32();
                    break;
                case FieldTabCurrentNavIndex when wire == 0:
                    currentNavIndex = reader.ReadInt32();
                    break;
                case FieldTabNavigation when wire == 2:
                    navigations.Add(ParseNavigation(reader.ReadMessage()));
                    break;
                case FieldTabPinned when wire == 0:
                    pinned = reader.ReadBool();
                    break;
                case FieldTabExtensionAppId when wire == 2:
                    extensionAppId = reader.ReadString();
                    break;
                case FieldTabLastActiveTime when wire == 0:
                    lastActiveTime = reader.ReadInt64();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParsedTab(tabId, windowId, tabVisualIndex, currentNavIndex,
            pinned, extensionAppId, lastActiveTime, navigations);
    }

    private static ParsedNavigation ParseNavigation(ProtobufReader reader)
    {
        string url = "", title = "", referrer = "";
        int pageTransition = 0, httpStatusCode = 0;
        long timestampMsec = 0;

        while (reader.HasData)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case FieldNavUrl when wire == 2:
                    url = reader.ReadString();
                    break;
                case FieldNavTitle when wire == 2:
                    title = reader.ReadString();
                    break;
                case FieldNavReferrer when wire == 2:
                    referrer = reader.ReadString();
                    break;
                case FieldNavPageTransition when wire == 0:
                    pageTransition = reader.ReadInt32();
                    break;
                case FieldNavTimestamp when wire == 0:
                    timestampMsec = reader.ReadInt64();
                    break;
                case FieldNavHttpStatusCode when wire == 0:
                    httpStatusCode = reader.ReadInt32();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new ParsedNavigation(url, title, referrer, pageTransition, timestampMsec, httpStatusCode);
    }
}
