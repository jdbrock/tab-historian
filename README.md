# 🕰️ Tab Historian

A .NET tool that snapshots all Chrome tabs across every window and profile, storing full navigation history in SQLite. Uses VSS shadow copies to read Chrome's locked session files. Includes a WPF search/browse GUI.

## 📸 What it captures

- 🌐 All open tabs across all Chrome profiles and windows
- 📜 Full navigation history per tab (URLs, titles, timestamps, HTTP status codes, referrers)
- 🪟 Window metadata (bounds, show state, type, active window)
- 📌 Tab metadata (pinned state, last active time, tab groups, extension app IDs)

## 📦 Projects

| Project | Description |
|---------|-------------|
| `TabHistorian` | CLI tool — discovers Chrome profiles, parses SNSS session files, saves a snapshot to SQLite, then exits. Designed to run via Task Scheduler. |
| `TabHistorian.Common` | Shared library — settings loader and read-only database access, referenced by all other projects. |
| `TabHistorian.Viewer` | WPF desktop app — search and browse tab history in a tree view (Snapshot > Profile > Window > Tab > Navigation History) with detail panel, favicons, and open-in-browser buttons. |
| `TabHistorian.Web` | ASP.NET minimal API + Next.js SPA — dark-themed web frontend with live search, infinite scroll, and hierarchical explorer. Listens on port 17000. |

## ⚙️ Requirements

- .NET 10 SDK
- Windows (Chrome on Windows stores session files in SNSS format)
- Google Chrome installed
- 🔒 Administrator privileges (required for VSS shadow copies to read Chrome's locked session files)

## 🚀 Usage

### Taking a snapshot

```
dotnet run --project src/TabHistorian
```

Takes a single snapshot and exits. Run elevated (as administrator) to read Chrome's locked session files via VSS.

### 🔧 Configuration

Settings are stored in `%USERPROFILE%\Documents\TabHistorian\settings.json` (created with defaults on first run):

```json
{
  "databasePath": "tabhistorian.db",
  "backupDirectory": "backups"
}
```

Relative paths resolve against the settings directory. Absolute paths (including UNC paths with forward slashes) are used as-is.

### ⏰ Scheduling with Task Scheduler

To take automatic snapshots every 30 minutes:

1. Build the project: `dotnet build src/TabHistorian`
2. Run `RegisterScheduledTask.bat` as Administrator

Or manually create a task pointing at `src\TabHistorian\bin\TabHistorian.exe` with highest privileges, repeating every 30 minutes.

### 🔍 Running the viewer

```
dotnet run --project src/TabHistorian.Viewer
```

- Type in the search box to filter tabs by URL, title, or navigation history
- Use the snapshot dropdown to view a specific point in time
- Expand snapshots > profiles > windows > tabs > navigation history in the tree
- Select any item to see full details in the right panel
- Open URLs directly in Chrome or Edge from the detail panel

## 🗂️ Snapshot retention

Old snapshots are automatically pruned after each run to keep the database manageable:

| Age | Kept |
|-----|------|
| Today | All snapshots |
| Yesterday | Oldest snapshot |
| 2–7 days ago | Oldest snapshot |
| 8–30 days ago | Oldest snapshot |
| Older than 30 days | Oldest per calendar month |

**Example** (today is March 4, 2026 with snapshots every 30 min):

| Snapshot | Reason |
|----------|--------|
| Mar 4 00:00 – now | ✅ Today — keep all |
| Mar 3 00:00 | ✅ Yesterday — oldest kept |
| Feb 26 00:00 | ✅ Previous week — oldest kept |
| Feb 4 00:00 | ✅ Previous month — oldest kept |
| Jan 1 00:00 | ✅ January — oldest kept |
| Dec 1 00:00 | ✅ December — oldest kept |
| Nov 1, Oct 1, ... | ✅ One per month |

## 🔧 How it works

1. **👤 Profile Discovery** — reads Chrome's `Local State` JSON to find all profile directories
2. **💾 VSS Shadow Copy** — creates a Volume Shadow Copy to read Chrome's exclusively-locked session files without interfering with the browser
3. **📄 Session Parsing** — copies SNSS session files from the shadow copy to temp, parses the binary command log to reconstruct window/tab state
4. **🗄️ Storage** — saves each snapshot to SQLite with full relational structure (snapshots > windows > tabs + JSON navigation history)

### SNSS format

Chrome stores session state as a binary command log. Each command updates a piece of state (set tab's window, update navigation entry, set pinned state, etc.). TabHistorian replays these commands to reconstruct the full session state at the time of the snapshot.

Key implementation details are documented in [LEARNINGS.md](LEARNINGS.md).

## 💾 Database backups

The database is automatically backed up daily (via `VACUUM INTO`) to the configured backup directory. Backups are named `tabhistorian-YYYY-MM-DD.db` and only one is created per day.

## 🛡️ Safety

- ✅ **All Chrome file access is strictly read-only** — files are read via VSS shadow copies or copied to temp before parsing
- ✅ VSS shadow copies are read-only, point-in-time snapshots — no risk of data loss or interference with Chrome
- ✅ The SQLite database is opened read-only by the Viewer and Web frontend
- ✅ Zero writes to any Chrome directory, ever
