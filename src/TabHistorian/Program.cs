using TabHistorian;
using TabHistorian.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<ChromeProfileDiscovery>();
builder.Services.AddSingleton<VssShadowCopy>();
builder.Services.AddSingleton<SessionFileReader>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<SnapshotService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
