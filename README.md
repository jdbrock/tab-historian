# TabHistorian

A .NET background service that snapshots all Chrome tabs across every window and profile every 30 minutes, storing full navigation history in SQLite. Includes a WPF search/browse GUI.

## What it captures

- All open tabs across all Chrome profiles and windows
- Full navigation history per tab (URLs, titles, timestamps, HTTP status codes, referrers)
- Window metadata (bounds, show state, type, active window)
- Tab metadata (pinned state, last active time, tab groups, extension app IDs)

## Projects

| Project | Description |
|---------|-------------|
| `TabHistorian` | Background worker service — discovers Chrome profiles, parses SNSS session files, saves snapshots to SQLite |
| `TabHistorian.Viewer` | WPF desktop app — search and browse tab history in a tree view (Snapshot > Window > Tab > Navigation History) |

## Requirements

- .NET 10 SDK
- Windows (Chrome on Windows stores session files in SNSS format)
- Google Chrome installed

## Usage

### Running the service

```
dotnet run --project src/TabHistorian
```

Takes an initial snapshot on startup, then every 30 minutes. The database is stored at:

```
%USERPROFILE%\Documents\TabHistorian\tabhistorian.db
```

### Running the viewer

```
dotnet run --project src/TabHistorian.Viewer
```

- Type in the search box to filter tabs by URL, title, or navigation history
- Use the snapshot dropdown to view a specific point in time
- Expand snapshots > windows > tabs > navigation history in the tree

## How it works

1. **Profile Discovery** — reads Chrome's `Local State` JSON to find all profile directories
2. **Session Parsing** — copies SNSS session files to temp (read-only access), parses the binary command log to reconstruct window/tab state
3. **Storage** — saves each snapshot to SQLite with full relational structure (snapshots > windows > tabs + JSON navigation history)

### SNSS format

Chrome stores session state as a binary command log. Each command updates a piece of state (set tab's window, update navigation entry, set pinned state, etc.). TabHistorian replays these commands to reconstruct the full session state at the time of the snapshot.

Key implementation details are documented in [LEARNINGS.md](LEARNINGS.md).

## Safety

- **All Chrome file access is strictly read-only** — files are copied to temp before parsing, opened with `FileShare.ReadWrite` to avoid interfering with Chrome
- The SQLite database is opened read-only by the Viewer
- Zero writes to any Chrome directory, ever
