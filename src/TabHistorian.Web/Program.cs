using System.Text.Json;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using TabHistorian.Common;
using TabHistorian.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:17000");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(origin =>
    new Uri(origin).Host == "localhost").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TabHistorianSettings.Load());
builder.Services.AddSingleton<TabHistorianDb>();
builder.Services.AddSingleton<TabMachineReader>();

var app = builder.Build();
app.UseCors();
app.UseStaticFiles();
app.MapOpenApi();
app.MapScalarApiReference();

// SSE: push "db-updated" events when the worker writes to either database
var settings = app.Services.GetRequiredService<TabHistorianSettings>();
var dbWatcher = new DatabaseWatcher(settings);

app.MapGet("/api/events", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    await ctx.Response.WriteAsync($"data: connected\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);

    using var sub = dbWatcher.Subscribe();
    while (!ct.IsCancellationRequested)
    {
        var db = await sub.WaitAsync(ct);
        await ctx.Response.WriteAsync($"data: {{\"type\":\"db-updated\",\"database\":\"{db}\"}}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});

var api = app.MapGroup("/api");

api.MapGet("/snapshots", (TabHistorianDb db) => db.GetSnapshots());

api.MapGet("/profiles", (TabHistorianDb db) => db.GetProfiles());

api.MapGet("/tabs", (TabHistorianDb db, string? q, long? snapshotId, string? profile, int? page, int? pageSize) =>
{
    var p = Math.Max(1, page ?? 1);
    var size = Math.Clamp(pageSize ?? 50, 1, 5000);
    var offset = (p - 1) * size;

    var totalCount = db.CountTabs(q, snapshotId, profile);
    var rows = db.SearchTabs(q, snapshotId, profile, offset, size);

    var items = rows.Select(r => new TabResult
    {
        SnapshotId = r.SnapshotId,
        SnapshotTimestamp = r.SnapshotTimestamp,
        WindowId = r.WindowId,
        ProfileName = r.ProfileName,
        ProfileDisplayName = r.ProfileDisplayName,
        WindowIndex = r.WindowIndex,
        TabId = r.TabId,
        TabIndex = r.TabIndex,
        CurrentUrl = r.CurrentUrl,
        Title = r.Title,
        Pinned = r.Pinned,
        LastActiveTime = r.LastActiveTime,
        NavigationHistory = r.NavigationHistory
    });

    return new { items, page = p, pageSize = size, totalCount };
});

// Tab Machine endpoints
var tm = api.MapGroup("/tabmachine");

tm.MapGet("/stats", (TabMachineReader db) => db.GetStats());

tm.MapGet("/profiles", (TabMachineReader db) => db.GetProfiles());

tm.MapGet("/search", (TabMachineReader db, string? q, string? profile, bool? isOpen, string? sort, int? page, int? pageSize) =>
{
    var p = Math.Max(1, page ?? 1);
    var size = Math.Clamp(pageSize ?? 50, 1, 5000);
    var offset = (p - 1) * size;
    var totalCount = db.CountSearch(q, profile, isOpen);
    var items = db.Search(q, profile, isOpen, sort, offset, size);
    return new { items, page = p, pageSize = size, totalCount };
});

tm.MapGet("/events", (TabMachineReader db, long? tabIdentityId, string? eventType, string? before, string? after, int? page, int? pageSize) =>
{
    var p = Math.Max(1, page ?? 1);
    var size = Math.Clamp(pageSize ?? 50, 1, 5000);
    var offset = (p - 1) * size;
    var totalCount = db.CountEvents(tabIdentityId, eventType, before, after);
    var items = db.GetEvents(tabIdentityId, eventType, before, after, offset, size);
    return new { items, page = p, pageSize = size, totalCount };
});

tm.MapGet("/timeline", (TabMachineReader db, string timestamp, string? profile) =>
    db.GetTimeline(timestamp, profile));

app.MapFallbackToFile("index.html");

app.Run();

record TabResult
{
    public long SnapshotId { get; init; }
    public string? SnapshotTimestamp { get; init; }
    public long WindowId { get; init; }
    public string? ProfileName { get; init; }
    public string? ProfileDisplayName { get; init; }
    public int WindowIndex { get; init; }
    public long TabId { get; init; }
    public int TabIndex { get; init; }
    public string? CurrentUrl { get; init; }
    public string? Title { get; init; }
    public bool Pinned { get; init; }
    public string? LastActiveTime { get; init; }

    [JsonConverter(typeof(RawJsonConverter))]
    public string? NavigationHistory { get; init; }
}

class RawJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString();

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
            writer.WriteNullValue();
        else
            writer.WriteRawValue(value);
    }
}
