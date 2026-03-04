using System.Text.Json;
using Scalar.AspNetCore;
using TabHistorian.Web.Data;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:17000");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(origin =>
    new Uri(origin).Host == "localhost").AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton<TabHistorianDb>();

var app = builder.Build();
app.UseCors();
app.MapOpenApi();
app.MapScalarApiReference();

var api = app.MapGroup("/api");

api.MapGet("/snapshots", (TabHistorianDb db) => db.GetSnapshots());

api.MapGet("/profiles", (TabHistorianDb db) => db.GetProfiles());

api.MapGet("/tabs", (TabHistorianDb db, string? q, long? snapshotId, string? profile, int? page, int? pageSize) =>
{
    var p = Math.Max(1, page ?? 1);
    var size = Math.Clamp(pageSize ?? 50, 1, 200);
    var offset = (p - 1) * size;

    var totalCount = db.CountTabs(q, snapshotId, profile);
    var rows = db.SearchTabs(q, snapshotId, profile, offset, size);

    var items = rows.Select(r => new
    {
        r.SnapshotId,
        r.SnapshotTimestamp,
        r.WindowId,
        r.ProfileName,
        r.ProfileDisplayName,
        r.WindowIndex,
        r.TabId,
        r.TabIndex,
        r.CurrentUrl,
        r.Title,
        r.Pinned,
        r.LastActiveTime,
        NavigationHistory = JsonSerializer.Deserialize<JsonElement>(r.NavigationHistory)
    });

    return new { items, page = p, pageSize = size, totalCount };
});

app.Run();
