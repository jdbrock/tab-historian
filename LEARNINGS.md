# TabHistorian - Technical Learnings

## SNSS Binary Format

### File Structure
- 8-byte header: uint32 magic `0x53534E53` ("SNSS") + int32 version (1 or 3)
- Then sequential command records until EOF
- Each record: uint16 size (of commandId + payload), uint8 commandId, byte[] payload
- Version 3 has marker command (ID 255) after header — skip it
- Modern Chrome (86+) stores files in `<Profile>/Sessions/` as `Session_<timestamp>` and `Tabs_<timestamp>`
- The timestamp in filenames is WebKit format (microseconds since 1601-01-01 UTC)

### Critical: SetTabWindow Payload Order
- **The payload is `(windowId, tabId)` — window ID comes FIRST**
- This is the opposite of what some documentation suggests
- Discovered by dumping raw payloads: the first int32 repeated across multiple commands (same window), second int32 was unique (different tabs)
- Getting this wrong results in 1:1 window-to-tab ratio (each tab gets its own "window")

### Session File Fallback
- When Chrome closes cleanly, the newest `Session_*` file may be empty/freshly initialized
- Must try files newest-to-oldest and fall back to the next one if no windows are found
- The second newest file typically has the valid previous session

### Pickle Format
- First 4 bytes of a pickle payload = uint32 payload size (skip it)
- All values aligned to 4-byte boundaries
- Strings: int32 byte_length, then bytes, then pad to 4-byte boundary
- **String16: int32 CHAR count (not byte count!), then `charCount * 2` bytes (UTF-16 LE), then pad to 4-byte boundary**
  - Getting this wrong halves the read, corrupts every field after the title
- Alignment: `(n + 3) & ~3`

### UpdateTabNavigation Pickle Layout (Command ID 6)
Full field sequence (must read in order, can't skip ahead):
```
[uint32 pickle_payload_size]  — skip (4 bytes)
[int32  tab_id]
[int32  nav_index]
[string virtual_url]           — UTF-8, the displayed URL
[string16 title]               — UTF-16 LE
[string encoded_page_state]    — binary blob, can be very large
[int32  transition_type]       — lower 8 bits = core type, upper = qualifiers
[int32  type_mask]             — bit 1 = HAS_POST_DATA
[string referrer_url]          — UTF-8
[int32  obsolete_referrer_policy]  — always 0
[string original_request_url]  — UTF-8, pre-redirect URL
[bool   is_overriding_user_agent]  — int32 (0 or 1)
[int64  timestamp]             — WebKit timestamp (microseconds since 1601-01-01)
[string16 search_terms]        — always empty
[int32  http_status_code]      — e.g. 200
[int32  referrer_policy]
[int32  extended_info_map_size]
  [string key, string value] × N
[int64  task_id]
[int64  parent_task_id]
[int64  root_task_id]
[int32  children_task_ids_size] — always 0
```

### WebKit Timestamps
- Microseconds since 1601-01-01 00:00:00 UTC
- Convert to .NET DateTime: `DateTime.FromFileTimeUtc(webkitTimestamp * 10)`
- Or: `new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(webkitTimestamp * 10)`

### Command ID Reference (Session files)
```
ID 0  - SetTabWindow: (windowId, tabId) — TWO int32s, window first!
ID 2  - SetTabIndexInWindow: (tabId, index)
ID 6  - UpdateTabNavigation: Pickle (see layout above)
ID 7  - SetSelectedNavigationIndex: (tabId, navIndex)
ID 8  - SetSelectedTabInIndex: (windowId, tabIndex)
ID 9  - SetWindowType: (windowId, type) — 0=normal, 1=popup, 2=app
ID 12 - SetPinnedState: int32 tabId + bool (8 bytes with padding)
ID 13 - SetExtensionAppID: Pickle (string)
ID 14 - SetWindowBounds3: int32 windowId, x, y, w, h, showState (24 bytes)
ID 15 - SetWindowAppName: Pickle (string)
ID 16 - TabClosed: int32 tabId + int64 closeTime
ID 17 - WindowClosed: int32 windowId + int64 closeTime
ID 19 - SessionStorageAssociated: Pickle (string)
ID 20 - SetActiveWindow: (windowId, _)
ID 21 - LastActiveTime: int32 tabId + int64 timestamp
ID 23 - SetWindowWorkspace2: Pickle (string)
ID 25 - SetTabGroup: int32 tabId, uint64 tokenHigh, uint64 tokenLow, bool hasGroup
ID 27 - SetTabGroupMetadata2: Pickle (group name + color)
ID 28 - SetTabGuid: Pickle (string)
ID 31 - SetWindowUserTitle: Pickle (string)
ID 32 - SetWindowVisibleOnAllWorkspaces: int32 windowId + bool
```

### IDAndIndexPayload Struct
- Two int32 values, 8 bytes total
- Meaning depends on the command (first = entity ID, second = value/index)
- Exception: SetTabWindow where first = windowId, second = tabId

### IDAndPayload64 Struct (used by LastActiveTime, command 21)
- **Has struct alignment padding on Windows**: int32 id (4 bytes) + 4 bytes padding + int64 payload (8 bytes) = **16 bytes total**
- The int64 timestamp is at **offset 8, not offset 4** — reading at offset 4 reads padding bytes + half the timestamp, producing garbage dates (e.g. year 0059 instead of 2024)

## WPF Project Notes
- WPF projects (`net10.0-windows`) don't get `System.IO` via implicit usings — need explicit `using System.IO`
- `dotnet new wpf` creates a nested directory when run inside an existing directory — need to flatten
- SQLite read-only mode: use `Mode=ReadOnly` in connection string

## Chrome Data Access
- ALWAYS open Chrome files with `FileAccess.Read` and `FileShare.ReadWrite`
- Copy to temp before parsing — never read in place
- `Local State` JSON → `profile.info_cache` has profile directories as keys, `name` field = display name
- Profiles on this machine: Default (Personal), Profile 2 (Trickle), Profile 5 (Vengeful Spirit), Profile 6 (X), Profile 7 (Kalopsia)

## Chrome Sync Data (LevelDB + Protobuf)

### LevelDB Reading
- Chrome's Sync Data LevelDB lives at `<Profile>/Sync Data/LevelDB/` (inside the Default profile, NOT at the User Data root)
- Chrome LevelDB uses Snappy compression — need `Snappier` NuGet package
- **Off-the-shelf LevelDB readers all failed**: IronLeveldb (too old, crashes on MANIFEST), LevelDB.Standard (native DLL lacks Snappy), RocksDbSharp (incompatible on-disk format)
- Custom `LevelDbReader.cs` reads .ldb SSTable files and .log WAL files directly
- Copy entire LevelDB dir to temp before reading — Chrome holds LOCK file exclusively
- Use `FileShare.ReadWrite | FileShare.Delete` when copying (skip files Chrome locks exclusively)
- LevelDB key format: `sessions-dt-<storage_key>` — iterate with this prefix
- LevelDB internal key: user_key + 8-byte suffix `(sequence_number << 8 | type)` — strip the suffix
- Deduplicate by sequence number (higher wins), respect deletion markers (type == 0)

### Protobuf Parsing

#### Wire Type Validation — CRITICAL
- **Always check wire type before reading a field value.** Chrome sometimes serializes fields with unexpected wire types (e.g., a bool field as length-delimited).
- Pattern: `case FieldX when wire == 0:` for varints, `case FieldY when wire == 2:` for strings/messages
- On wire type mismatch, fall through to `default: reader.SkipField(wire)`
- Without this, the parser reads wrong bytes and desyncs, producing cascading "Unknown wire type" errors

#### SessionSpecifics Proto
```
Field 1: session_tag (string)
Field 2: header (SessionHeader message)
Field 3: tab (SessionTab message)
Field 4: tab_node_id (int32) — DO NOT use for tab lookup
```

#### SessionHeader Proto
```
Field 2: window (repeated SessionWindow message)
Field 3: client_name (string) — device display name
```

#### SessionWindow Proto
```
Field 1: window_id (int32)
Field 2: selected_tab_index (int32)
Field 3: browser_type (enum)
Field 4: tab (repeated int32) — these are tab_id values, NOT tab_node_ids
```

#### SessionTab Proto — FIELD NUMBERS DIFFER FROM OLD DOCS
```
Field 1:  tab_id (int32) — the key used by SessionWindow.tab
Field 2:  window_id (int32)
Field 3:  tab_visual_index (int32)
Field 4:  current_navigation_index (int32)
Field 5:  pinned (bool)              ← NOT field 7!
Field 6:  extension_app_id (string)  ← NOT field 8!
Field 7:  navigation (repeated TabNavigation)  ← NOT field 6!
Field 14: last_active_time_unix_epoch_millis (int64)
```
- **Many online references and older Chrome versions show pinned=7, navigation=6, extension_app_id=8 — these are WRONG for modern Chrome.**
- Verified against `chromium.googlesource.com` canonical proto definition.

#### TabNavigation Proto
```
Field 2:  virtual_url (string)
Field 3:  referrer (string)
Field 4:  title (string)
Field 6:  page_transition (int32)
Field 9:  timestamp_msec (int64)
Field 15: http_status_code (int32)
```

### Key Gotchas
1. **Tab lookup key**: `SessionWindow.tab` values match `SessionTab.tab_id` (field 1), NOT `SessionSpecifics.tab_node_id` (field 4). Using tab_node_id produces zero matches.
2. **Protobuf group wire types**: Chrome data contains deprecated group wire types (3=start group, 4=end group). The ProtobufReader.SkipField must handle these or parsing fails.
3. **Synced device profiles**: Use `synced:<session_tag>` as the stable profile name and `Remote: <client_name>` as the display name. Synced tabs flow through existing ChromeWindow/ChromeTab/NavigationEntry models with no schema changes.
