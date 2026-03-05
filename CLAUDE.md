# TabHistorian - Project Guidelines

## Git Conventions
- **Never** add a `Co-Authored-By` line to commits

## CRITICAL SAFETY RULES
- **NEVER write to anything in the Chrome User Data directory** (`%LOCALAPPDATA%\Google\Chrome\User Data\`)
- Always operate **READ-ONLY** on Chrome files — never open them in read/write mode
- Copy session files to a temp location before reading — never modify originals
- Zero tolerance for any risk of Chrome data loss
- When opening Chrome files, always use `FileShare.ReadWrite` to avoid interfering with Chrome's own locks
- **NEVER delete the TabHistorian database** — if asked, triple-confirm with the user before proceeding
- Always use **migrations** when changing the database schema — never rely on deleting/recreating the DB

## Project Overview
- .NET 10 / C# background service backing up Chrome tabs every 30 minutes
- Parses SNSS binary format from Chrome session files
- Stores snapshots in SQLite

## Tech Stack
- .NET 10, Worker Service pattern (`Microsoft.Extensions.Hosting`)
- `Microsoft.Data.Sqlite` for storage
- Target: Windows

## Chrome Data Location
- User Data: `%LOCALAPPDATA%\Google\Chrome\User Data\`
- Modern session files in `<Profile>/Sessions/` named `Session_<timestamp>`, `Tabs_<timestamp>`
- `Local State` JSON in User Data root has profile info
- SNSS format: 8-byte header (magic `0x53534E53` + version), then sequential command records

## Project Structure
```
src/TabHistorian.Common/         — Shared settings loader + read-only DB access
src/TabHistorian/
├── Program.cs / Worker.cs       — Entry point + background service
├── Models/                      — Snapshot, ChromeWindow, ChromeTab, NavigationEntry
├── Services/                    — ProfileDiscovery, SessionFileReader, SnapshotService, StorageService
└── Parsing/                     — SnssParser, PickleReader
src/TabHistorian.Viewer/         — WPF desktop app
src/TabHistorian.Web/            — ASP.NET minimal API + Next.js SPA (frontend/)
```
