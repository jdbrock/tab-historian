using Serilog;
using TabHistorian;
using TabHistorian.Services;
using TabHistorian.Common;

using var mutex = new Mutex(true, @"Global\TabHistorian", out bool createdNew);
if (!createdNew)
{
    Console.Error.WriteLine("Another instance of TabHistorian is already running.");
    return 1;
}

var settings = TabHistorianSettings.Load();
var logPath = Path.Combine(settings.SettingsDirectory, "logs", "tabhistorian-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

Log.Information("================ TabHistorian starting (PID {Pid}) ================", Environment.ProcessId);

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<ChromeProfileDiscovery>();
    builder.Services.AddSingleton<VssShadowCopy>();
    builder.Services.AddSingleton<SessionFileReader>();
    builder.Services.AddSingleton<StorageService>();
    builder.Services.AddSingleton<SyncedSessionReader>();
    builder.Services.AddSingleton<TabMachineDb>();
    builder.Services.AddSingleton<TabMachineService>();
    builder.Services.AddSingleton<SnapshotService>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();

    // Safety timeout: force-exit if the process hangs, so the mutex is released
    // and the next scheduled run isn't blocked indefinitely
    var timeout = TimeSpan.FromMinutes(2);
    using var cts = new CancellationTokenSource(timeout);
    cts.Token.Register(() =>
    {
        Log.Error("Process exceeded {Timeout} minute timeout — force exiting", timeout.TotalMinutes);
        Log.CloseAndFlush();
        Environment.Exit(2);
    });

    host.Run();
}
finally
{
    Log.CloseAndFlush();
}

return 0;
