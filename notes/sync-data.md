# Chrome Sync Data Details

## LevelDB Reading
- Chrome LevelDB uses Snappy compression — need `Snappier` NuGet package
- Off-the-shelf LevelDB readers don't work (IronLeveldb, LevelDB.Standard, RocksDbSharp all failed)
- Custom `LevelDbReader.cs` handles .ldb table files and .log WAL files
- Copy LevelDB to temp before reading — Chrome holds LOCK file exclusively

## Protobuf Field Numbers (from chromium source)

### SessionSpecifics
- 1: session_tag (string)
- 2: header (SessionHeader message)
- 3: tab (SessionTab message)
- 4: tab_node_id (int32) — NOT used for tab lookup; use SessionTab.tab_id instead

### SessionHeader
- 2: window (repeated SessionWindow message)
- 3: client_name (string)

### SessionWindow
- 1: window_id (int32)
- 2: selected_tab_index (int32)
- 3: browser_type (enum)
- 4: tab (repeated int32) — these are tab_id values matching SessionTab.tab_id

### SessionTab — IMPORTANT: field numbers differ from old docs!
- 1: tab_id (int32) — matches SessionWindow.tab values
- 2: window_id (int32)
- 3: tab_visual_index (int32)
- 4: current_navigation_index (int32)
- **5: pinned (bool)** — NOT field 7!
- **6: extension_app_id (string)** — NOT field 8!
- **7: navigation (repeated TabNavigation)** — NOT field 6!
- 14: last_active_time_unix_epoch_millis (int64)

### TabNavigation
- 2: virtual_url (string)
- 3: referrer (string)
- 4: title (string)
- 6: page_transition (int32)
- 9: timestamp_msec (int64)
- 15: http_status_code (int32)

## Key Gotchas
1. **Wire type validation**: Always check wire type matches expected before reading. Chrome sometimes uses unexpected wire types. Fall through to SkipField on mismatch.
2. **Tab lookup key**: Windows reference tabs by SessionTab.tab_id (field 1), NOT by SessionSpecifics.tab_node_id (field 4).
3. **SessionTab field numbering**: Many online references show old field numbers (pinned=7, navigation=6). The actual Chrome proto has pinned=5, extension_app_id=6, navigation=7.
