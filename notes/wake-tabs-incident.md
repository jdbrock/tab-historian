# Wake Tabs Extension Incident (2026-03-06)

## What Happened
Created a Chrome extension (`tools/wake-tabs-extension/`) to activate all sleeping/discarded tabs to force Chrome sync. Running it caused widespread data contamination in the TabMachine DB.

## Chrome Extension
- Located at `tools/wake-tabs-extension/` (manifest v3, service worker + popup)
- Iterates all tabs across all windows, briefly activates each to wake it
- Service worker keeps running even if popup closes

## Problems Caused by Waking Tabs

### 1. Spurious TitleChanged Events
- Chrome prefixes sleeping tab titles with `💤 ` emoji
- Waking tabs removes the prefix, triggering TitleChanged events in TabMachine
- **Fix (code)**: `NormalizeTitle()` in `TabMachineService.ComputeDelta()` strips `💤` prefix before comparing titles
- **Fix (data)**: Deleted all TitleChanged events where title matched state_delta title

### 2. Navigation History Contamination
- Chrome appends a new nav entry when waking a sleeping tab (navigates back to current URL)
- This creates Updated events with `navigationHistory` changes
- The new nav entries have today's timestamps, making tabs appear recently active
- Nav entries from wake have same URL as earlier entry but different timestamp

### 3. last_active_time Inflation
- Chrome updates internal `lastActiveTime` when a tab is activated
- All 3200+ woken tabs got today's timestamp even though user didn't interact
- **Fix (data)**: `last_active_time = first_active_time` for tabs with only Opened events

### 4. last_seen Inflation
- Every event updates `last_seen` on `tab_identities`
- Spurious events bumped `last_seen` to today for thousands of old tabs
- **Fix (data)**: Reset from `MAX(timestamp)` of remaining events, then `MAX(last_seen, last_active_time)`

### 5. 💤 Titles Stored in DB
- `first_title`, `last_title` in `tab_identities` and `title` in `tab_current_state` had 💤 prefix
- **SQLite SUBSTR gotcha**: `SUBSTR(title, 4)` with hardcoded offsets is WRONG for multi-byte emoji — use `SUBSTR(title, 1 + LENGTH('💤 '))` to get correct character-based offset

### 6. last_navigated Not Populated
- `last_navigated` was only set on Navigated events (URL change), NULL for most tabs
- **Fix (code)**: `GetLastRealNavTimestamp()` parses nav history JSON, finds last entry that isn't a sleep/wake transition (same URL + title differs only by 💤 prefix)
- **Fix (code)**: `UpdateTabIdentity()` now accepts `lastNavTime` from nav history instead of just the event timestamp
- **Fix (data)**: Python script `tools/fix-last-navigated.py` — for wake-contaminated tabs (only Opened/Updated events), uses nav history from the Opened event's state_delta (original, uncontaminated data) instead of current_state

## Sort Order Fix
- "All tabs" view sort changed from `last_active_time DESC` to `COALESCE(last_navigated, last_active_time) DESC` in `TabMachineReader.Search()`
- This uses real navigation timestamps rather than Chrome's internal active time

## Code Changes Made
1. `TabMachineService.cs`: `NormalizeTitle()` — strips 💤 prefix for title comparison
2. `TabMachineService.cs`: `GetLastRealNavTimestamp()` — parses nav history, skips sleep/wake entries
3. `TabMachineService.cs`: `IsSleepWakeTitleChange()` — detects 💤-only title diffs
4. `TabMachineService.cs`: `UpdateTabIdentity()` — now updates `last_navigated` from nav history timestamps
5. `TabMachineReader.cs`: Sort order changed to `COALESCE(last_navigated, last_active_time) DESC`

## Data Fix Scripts
- `tools/fix-last-navigated.py` — one-time script to populate `last_navigated` from nav history, using Opened event data for wake-contaminated tabs

## Key Lessons
- Activating Chrome tabs has wide side effects: title changes, nav history additions, lastActiveTime updates
- Always pause TabHistorian worker before running bulk Chrome operations
- SQLite SUBSTR with emoji: use `LENGTH()` not hardcoded byte offsets
- `state_delta` in events stores NEW values, not old values — can't see what prev state was
- The Opened event's `state_delta` contains the full original state including nav history — useful for recovery
