# Chrome Sync Data LevelDB Internals

Technical reference for reading Chrome's synced session data. Gathered from Chromium source code analysis.

## File Location

```
<Profile>/Sync Data/LevelDB/
├── *.ldb          — sorted string table files (key-value data)
├── *.log          — write-ahead log
├── CURRENT        — points to active MANIFEST
├── MANIFEST-*     — database metadata (which .ldb files, key ranges)
├── LOCK           — Chrome holds this while running
├── LOG / LOG.old  — LevelDB operational logs
```

Typical size: under 5MB. Chrome holds a lock on this directory — **must copy to temp before reading**.

Not all profiles have sync enabled. If `Sync Data/LevelDB/` doesn't exist, skip that profile.

Multiple local profiles may sync to the same Google account — read from one profile to avoid duplicates.

## LevelDB Key Namespace

The Sync Data LevelDB is shared across all sync datatypes. Keys are namespaced by prefix.

Source: `components/sync/model/blocking_data_type_store_impl.cc`

| Key pattern | Purpose | Value format |
|-------------|---------|--------------|
| `sessions-dt-<storage_key>` | Session data record | `SessionSpecifics` protobuf |
| `sessions-md-<storage_key>` | Per-entity sync metadata | `EntityMetadata` protobuf |
| `sessions-GlobalMetadata` | Type-wide sync state | `DataTypeState` protobuf |

The prefix comes from `DataTypeToStableLowerCaseString(SESSIONS)` → `"sessions"`.

Constants: `-dt-` = data, `-md-` = metadata, `-GlobalMetadata` = global.

For account-scoped storage (non-default), keys get an `A-` prefix (e.g., `A-sessions-dt-...`).

## Storage Key Encoding

The `<storage_key>` portion is a **Chromium Pickle** binary blob (same format as PickleReader already handles).

Source: `components/sync_sessions/session_store.cc` → `EncodeStorageKey()`

```
Pickle {
    WriteString(session_tag)   // sync cache GUID string
    WriteInt(tab_node_id)      // -1 for headers, >=0 for tabs
}
```

Pickle binary layout:
- 4 bytes: uint32 payload_size (little-endian)
- Payload fields, each 4-byte aligned:
  - WriteString: int32 length + char[length] + padding
  - WriteInt: int32 value

Note: We don't strictly need to decode the storage key since `session_tag` and `tab_node_id` are also present in the protobuf value. But the PickleReader could be used if needed.

## Record Types

Each synced device produces two kinds of records:

### Header Record (tab_node_id = -1)
- One per device
- Contains: device name, device form factor, list of windows
- Each window contains a list of **tab ID references** (not full tab data)
- The tab IDs reference `SessionTab.tab_id` values in the tab records

### Tab Record (tab_node_id >= 0)
- One per tab across all windows on the device
- Contains: full tab data including URL, title, navigation history, pinned state
- `tab_node_id` is a sync-internal ID, NOT the same as `SessionTab.tab_id`
- `SessionTab.tab_id` is what the header's window tab references point to

## Assembly Algorithm

```
1. Iterate all keys with prefix "sessions-dt-"
2. Deserialize each value as SessionSpecifics protobuf
3. Group records by session_tag
4. For each session_tag group:
   a. Find the header record (tab_node_id == -1) → get device name + windows
   b. Collect all tab records (tab_node_id >= 0)
   c. Build a lookup: SessionTab.tab_id → tab record
   d. For each window in the header:
      - Resolve each tab ID reference to the actual tab record
      - Build ChromeWindow with resolved ChromeTab list
   e. Skip windows with no resolved tabs
   f. Skip devices with no windows
```

## Foreign vs Local Sessions

Source: `components/sync_sessions/session_store.cc`

- Each Chrome instance has a **local cache GUID** (a unique string per sync client)
- The local device's records use this GUID as `session_tag`
- All other `session_tag` values are **foreign sessions** (other devices)
- To find the local GUID: check `sessions-GlobalMetadata` or `sessions-md-*` metadata records
- Simpler approach: just include all sessions and let the existing local SNSS data coexist (synced profile names are prefixed `synced:` so they won't collide)

## Timestamp Format

Synced session timestamps use **milliseconds since Unix epoch** (1970-01-01).

This is DIFFERENT from SNSS session files which use WebKit epoch (1601-01-01, microseconds).

```csharp
// Sync timestamps (milliseconds since Unix epoch)
DateTime.UnixEpoch.AddMilliseconds(timestampMsec)

// vs SNSS timestamps (microseconds since WebKit epoch 1601-01-01)
new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(webkitTimestamp * 10)
```

## Protobuf Wire Format Quick Reference

Since we use a manual decoder:

| Wire type | Meaning | Encoding |
|-----------|---------|----------|
| 0 | Varint | int32, int64, bool, enum |
| 1 | 64-bit fixed | fixed64, double |
| 2 | Length-delimited | string, bytes, embedded messages, packed repeated |
| 5 | 32-bit fixed | fixed32, float |

Tag encoding: `(field_number << 3) | wire_type`

Varint encoding: 7 bits per byte, MSB = continuation bit.

Length-delimited: varint length prefix, then that many bytes.

Embedded messages are wire type 2 — read the bytes, then parse recursively.

Repeated fields: same field number appears multiple times (or packed in wire type 2 for scalars).

## .NET Library Options for LevelDB

| Library | Notes |
|---------|-------|
| **IronLeveldb** (1.0.0) | Pure managed C#, read-only, .NET Standard 1.3. Last updated 2018. May have issues with Snappy compression. |
| **LevelDB.Net_All** (2.2.3) | Native P/Invoke bindings to C++ LevelDB. Works but adds native binary dependencies. |
| **tvandijck/LevelDB.NET** | Pure C# read-only, not on NuGet (GitHub only). Barely maintained. |
| **Snappier** | If manual LevelDB reading needed, this provides pure C# Snappy decompression. |

## Chromium Source References

- [session_specifics.proto](https://github.com/chromium/chromium/blob/main/components/sync/protocol/session_specifics.proto) — Protobuf definitions
- [entity_specifics.proto](https://github.com/chromium/chromium/blob/main/components/sync/protocol/entity_specifics.proto) — SessionSpecifics is field 50119
- [blocking_data_type_store_impl.cc](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/sync/model/blocking_data_type_store_impl.cc) — Key prefix format
- [session_store.cc](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/sync_sessions/session_store.cc) — Storage key encoding, local vs foreign
- [data_type.cc](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/sync/base/data_type.cc) — `DataTypeToStableLowerCaseString`
- [session_sync_bridge.cc](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/sync_sessions/session_sync_bridge.cc) — Sync merge logic
- [base/pickle.h](https://chromium.googlesource.com/chromium/src/+/master/base/pickle.h) — Pickle binary format

## External References

- [David Bieber — Programmatically Accessing Chrome's Tabs from Other Devices](https://davidbieber.com/snippets/2021-01-01-programmatically-accessing-chromes-tabs-from-other-devices-data/) — Python approach using plyvel
- [ccl_chromium_reader](https://github.com/cclgroupltd/ccl_chromium_reader) — Python forensics tool for Chrome LevelDB
- [Chrome Sync Model API](https://www.chromium.org/developers/design-documents/sync/model-api/)
