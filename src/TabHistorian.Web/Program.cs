using System.Text.Json;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using TabHistorian.Common;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:17000");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(origin =>
    new Uri(origin).Host == "localhost").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TabHistorianSettings.Load());
builder.Services.AddSingleton<TabHistorianDb>();

var app = builder.Build();
app.UseCors();
app.UseStaticFiles();
app.MapOpenApi();
app.MapScalarApiReference();

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
