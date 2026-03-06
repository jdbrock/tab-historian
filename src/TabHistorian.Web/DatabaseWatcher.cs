using System.Threading.Channels;
using TabHistorian.Common;

namespace TabHistorian.Web;

/// <summary>
/// Watches the snapshot and tab machine database files for changes
/// and notifies SSE subscribers when either is modified.
/// </summary>
public sealed class DatabaseWatcher : IDisposable
{
    private readonly FileSystemWatcher[] _watchers;
    private readonly List<Channel<string>> _subscribers = [];
    private readonly Lock _lock = new();
    private DateTime _lastSnapshotNotify;
    private DateTime _lastTabMachineNotify;

    public DatabaseWatcher(TabHistorianSettings settings)
    {
        _watchers = [
            ..CreateWatchers(settings.ResolvedDatabasePath, "snapshot"),
            ..CreateWatchers(settings.ResolvedTabMachineDatabasePath, "tabmachine"),
        ];
    }

    private FileSystemWatcher[] CreateWatchers(string dbPath, string dbName)
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        var file = Path.GetFileName(dbPath);

        FileSystemWatcher MakeWatcher(string filter)
        {
            var w = new FileSystemWatcher(dir, filter)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Changed += (_, _) => OnChanged(dbName);
            return w;
        }

        // Watch both the DB file and the WAL file — SQLite writes to WAL first
        return [MakeWatcher(file), MakeWatcher(file + "-wal")];
    }

    private void OnChanged(string dbName)
    {
        // Debounce: ignore events within 2 seconds of the last notification per DB
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (dbName == "snapshot")
            {
                if ((now - _lastSnapshotNotify).TotalSeconds < 2) return;
                _lastSnapshotNotify = now;
            }
            else
            {
                if ((now - _lastTabMachineNotify).TotalSeconds < 2) return;
                _lastTabMachineNotify = now;
            }

            foreach (var ch in _subscribers)
                ch.Writer.TryWrite(dbName);
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (_lock)
            _subscribers.Add(channel);

        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<string> channel)
    {
        lock (_lock)
            _subscribers.Remove(channel);
    }

    public sealed class Subscription(DatabaseWatcher owner, Channel<string> channel) : IDisposable
    {
        public ValueTask<string> WaitAsync(CancellationToken ct) => channel.Reader.ReadAsync(ct);
        public void Dispose() => owner.Unsubscribe(channel);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
            w.Dispose();
    }
}
