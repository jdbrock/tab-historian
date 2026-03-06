<p align="center">
  <img src="art/logo.png" alt="Tab Historian" width="256" />
</p>

A .NET tool that snapshots all Chrome tabs across every window and profile, storing full navigation history in SQLite. Uses VSS shadow copies to read Chrome's locked session files. Includes a WPF desktop viewer and a web frontend with event-sourced tab tracking.

## What it captures

- All open tabs across all Chrome profiles and windows
- Full navigation history per tab (URLs, titles, timestamps, HTTP status codes, referrers)
- Window metadata (bounds, show state, type, active window)
- Tab metadata (pinned state, last active time, tab groups, extension app IDs)
- Synced tabs from other devices (via Chrome Sync Data / LevelDB)

## Projects

| Project | Description |
|---------|-------------|
| `TabHistorian` | CLI tool — discovers Chrome profiles, parses SNSS session files, saves a snapshot to SQLite, then exits. Designed to run via Task Scheduler. |
| `TabHistorian.Common` | Shared library — settings loader and read-only database access, referenced by all other projects. |
| `TabHistorian.Viewer` | WPF desktop app — search and browse tab history in a tree view (Snapshot > Profile > Window > Tab > Navigation History) with detail panel, favicons, and open-in-browser buttons. |
| `TabHistorian.Web` | ASP.NET minimal API + Next.js SPA — dark-themed web frontend with Tab Machine (event-sourced tab tracking with search and time travel), full snapshot browser with live search and infinite scroll, and hierarchical explorer. Listens on port 17000. |
| `TabHistorian.ChromeExtension` | Chrome sidebar extension — embeds the web frontend in a side panel for quick access while browsing. |

## Requirements

- .NET 10 SDK
- Windows (Chrome on Windows stores session files in SNSS format)
- Google Chrome installed
- Administrator privileges (required for VSS shadow copies to read Chrome's locked session files)

## Usage

### Taking a snapshot

```
dotnet run --project src/TabHistorian
```

Takes a single snapshot and exits. Run elevated (as administrator) to read Chrome's locked session files via VSS.

### Configuration

Settings are stored in `%USERPROFILE%\Documents\TabHistorian\settings.json` (created with defaults on first run):

```json
{
  "databasePath": "tabhistorian.db",
  "backupDirectory": "backups"
}
```

Relative paths resolve against the settings directory. Absolute paths (including UNC paths with forward slashes) are used as-is.

### Scheduling with Task Scheduler

To take automatic snapshots every 30 minutes:

1. Build the project: `dotnet build src/TabHistorian`
2. Run `RegisterScheduledTask.bat` as Administrator

Or manually create a task pointing at `src\TabHistorian\bin\TabHistorian.exe` with highest privileges, repeating every 30 minutes.

### Running the viewer

```
dotnet run --project src/TabHistorian.Viewer
```

- Type in the search box to filter tabs by URL, title, or navigation history
- Use the snapshot dropdown to view a specific point in time
- Expand snapshots > profiles > windows > tabs > navigation history in the tree
- Select any item to see full details in the right panel
- Open URLs directly in Chrome or Edge from the detail panel

### Running the web frontend

```
dotnet run --project src/TabHistorian.Web
```

Opens on `http://localhost:17000`. The web frontend includes:

- **Tab Machine** — event-sourced tab tracking with search across all tracked tabs and time travel to view tab state at any point in history
- **Full Snapshots** — browse snapshots with live search, infinite scroll, and profile filtering
- **Explorer** — hierarchical drill-down through snapshots, profiles, windows, and tabs

### Chrome sidebar extension

Load the extension from `src/TabHistorian.ChromeExtension` via `chrome://extensions` (Developer mode > Load unpacked). Click the Tab Historian icon in the toolbar to open the web frontend in a sidebar panel.

## Tab Machine

The Tab Machine tracks individual tab identities across snapshots using multi-pass matching heuristics:

1. **SyncTabNodeId** — exact match for synced/remote tabs
2. **Navigation history** — exact sequence match (2+ entries)
3. **Navigation prefix** — tab navigated forward since last snapshot
4. **Single-entry URL** — profile + URL match for tabs with one navigation entry

Events (Opened, Closed, TitleChanged, NavigatedTo) are stored as deltas, enabling efficient time travel queries without replaying full snapshots.

## Snapshot retention

Old snapshots are automatically pruned after each run to keep the database manageable:

| Age | Kept |
|-----|------|
| Today | All snapshots |
| Yesterday | Oldest snapshot |
| 2–7 days ago | Oldest snapshot |
| 8–30 days ago | Oldest snapshot |
| Older than 30 days | Oldest per calendar month |

## How it works

1. **Profile Discovery** — reads Chrome's `Local State` JSON to find all profile directories
2. **VSS Shadow Copy** — creates a Volume Shadow Copy to read Chrome's exclusively-locked session files without interfering with the browser
3. **Session Parsing** — copies SNSS session files from the shadow copy to temp, parses the binary command log to reconstruct window/tab state
4. **Sync Data** — reads Chrome's Sync Data LevelDB to capture tabs from other devices
5. **Tab Machine** — diffs current tabs against previous state to track tab identities and record events
6. **Storage** — saves each snapshot to SQLite with full relational structure (snapshots > windows > tabs + JSON navigation history)

### SNSS format

Chrome stores session state as a binary command log. Each command updates a piece of state (set tab's window, update navigation entry, set pinned state, etc.). TabHistorian replays these commands to reconstruct the full session state at the time of the snapshot.

Key implementation details are documented in [LEARNINGS.md](LEARNINGS.md).

## Database backups

The database is automatically backed up daily (via `VACUUM INTO`) to the configured backup directory. Backups are named `tabhistorian-YYYY-MM-DD.db` and only one is created per day.

## Safety

- All Chrome file access is strictly read-only — files are read via VSS shadow copies or copied to temp before parsing
- VSS shadow copies are read-only, point-in-time snapshots — no risk of data loss or interference with Chrome
- The SQLite database is opened read-only by the Viewer and Web frontend
- Zero writes to any Chrome directory, ever
