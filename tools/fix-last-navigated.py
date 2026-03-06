"""One-time fix: populate last_navigated from navigation history, skipping sleep/wake transitions.
For tabs with only Opened/Updated events (wake-contaminated), use the Opened event's nav history."""
import sqlite3
import json

DB = r"C:\ProgramData\TabHistorian\TabMachine.db"

def normalize_title(title):
    if not title:
        return ""
    if title.startswith("\U0001F4A4 "):
        return title[3:]
    if title.startswith("\U0001F4A4"):
        return title[2:]
    return title

def get_last_real_nav_ts(entries):
    last_ts = None
    prev_url = None
    prev_title = None

    for entry in entries:
        url = entry.get("url", "")
        title = entry.get("title", "")
        ts = entry.get("timestamp")

        if prev_url is not None and url == prev_url and normalize_title(prev_title) == normalize_title(title):
            prev_title = title
            continue

        if ts:
            last_ts = ts
        prev_url = url
        prev_title = title

    return last_ts

conn = sqlite3.connect(DB)
cur = conn.cursor()

# Find which identities only have Opened/Updated events (wake-contaminated)
wake_only = set(row[0] for row in cur.execute("""
    SELECT tab_identity_id FROM tab_events
    GROUP BY tab_identity_id
    HAVING SUM(CASE WHEN event_type NOT IN ('Opened', 'Updated') THEN 1 ELSE 0 END) = 0
""").fetchall())

# Get current state nav history for all tabs
current_rows = {row[0]: row[1] for row in cur.execute("""
    SELECT tab_identity_id, navigation_history
    FROM tab_current_state
    WHERE navigation_history IS NOT NULL AND navigation_history <> '[]'
""").fetchall()}

# Get Opened event nav history for wake-contaminated tabs
opened_nav = {}
for row in cur.execute("""
    SELECT tab_identity_id, state_delta FROM tab_events WHERE event_type = 'Opened'
""").fetchall():
    if row[0] in wake_only:
        try:
            delta = json.loads(row[1])
            nh = delta.get("navigationHistory")
            if nh:
                opened_nav[row[0]] = nh
        except (json.JSONDecodeError, TypeError):
            pass

updates = []
for identity_id in current_rows:
    # For wake-contaminated tabs, prefer the Opened event's nav history
    if identity_id in wake_only and identity_id in opened_nav:
        nav_json = opened_nav[identity_id]
    else:
        nav_json = current_rows[identity_id]

    try:
        entries = json.loads(nav_json)
    except json.JSONDecodeError:
        continue

    ts = get_last_real_nav_ts(entries)
    if ts:
        updates.append((ts, identity_id))

print(f"Updating last_navigated for {len(updates)} of {len(current_rows)} identities")
print(f"  ({len(wake_only)} wake-contaminated, {len(opened_nav)} with Opened nav history)")

cur.executemany("UPDATE tab_identities SET last_navigated = ? WHERE id = ?", updates)
conn.commit()

sample = cur.execute("""
    SELECT id, last_navigated, last_active_time
    FROM tab_identities WHERE id IN (189, 22, 82, 2484)
""").fetchall()
for row in sample:
    print(f"  id={row[0]}  last_navigated={row[1]}  last_active={row[2]}")

conn.close()
print("Done.")
