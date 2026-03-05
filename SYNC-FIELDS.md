# Chrome Sync Session Protobuf Field Mapping

Reference for mapping Chrome's `SessionSpecifics` protobuf messages (from `Sync Data/LevelDB/`) to TabHistorian's existing models.

Source: [Chromium session_specifics.proto](https://github.com/chromium/chromium/blob/main/components/sync/protocol/session_specifics.proto)

## LevelDB Key Format

- Keys prefixed `sessions-dt-<storage_key>` contain session data
- Storage key is a Pickle-encoded `(session_tag: string, tab_node_id: int)` pair
- `tab_node_id = -1` → header record (device info + window structure)
- `tab_node_id >= 0` → tab record

## SessionSpecifics (top-level message)

| # | Field | Type | Maps to | Status |
|---|-------|------|---------|--------|
| 1 | session_tag | string | ChromeWindow.ProfileName (`synced:<tag>`) | Mapped |
| 2 | header | SessionHeader | (container) | Mapped |
| 3 | tab | SessionTab | (container) | Mapped |
| 4 | tab_node_id | int32 | (internal routing, -1 = header) | Used internally |

## SessionHeader

| # | Field | Type | Maps to | Status |
|---|-------|------|---------|--------|
| 2 | window | repeated SessionWindow | (container) | Mapped |
| 3 | client_name | string | ChromeWindow.ProfileDisplayName | Mapped |
| 4 | device_type | DeviceType | — | Skipped (deprecated) |
| 5 | device_form_factor | DeviceFormFactor | — | Not mapped yet |
| 6 | session_start_time_unix_epoch_millis | int64 | — | Not mapped yet |

### DeviceFormFactor enum

| Value | Meaning |
|-------|---------|
| 0 | UNSPECIFIED |
| 1 | DESKTOP |
| 2 | PHONE |
| 3 | TABLET |
| 4 | AUTOMOTIVE |
| 5 | WEARABLE |
| 6 | TV |

## SessionWindow

| # | Field | Type | Maps to | Status |
|---|-------|------|---------|--------|
| 1 | window_id | int32 | (used for tab→window linkage) | Used internally |
| 2 | selected_tab_index | int32 | ChromeWindow.SelectedTabIndex | Mapped |
| 3 | browser_type | BrowserType | ChromeWindow.WindowType | Mapped |
| 4 | tab | repeated int32 | (tab ID references into SessionTab records) | Used internally |

### BrowserType enum

| Value | Meaning |
|-------|---------|
| 0 | UNKNOWN |
| 1 | TYPE_TABBED |
| 2 | TYPE_POPUP |
| 3 | TYPE_CUSTOM_TAB |
| 4 | TYPE_AUTH_TAB |

## SessionTab

| # | Field | Type | Maps to | Status |
|---|-------|------|---------|--------|
| 1 | tab_id | int32 | (used for window→tab linkage) | Used internally |
| 2 | window_id | int32 | (used for window→tab linkage) | Used internally |
| 3 | tab_visual_index | int32 | ChromeTab.TabIndex | Mapped |
| 4 | current_navigation_index | int32 | (selects current URL/title from navigation[]) | Used internally |
| 5 | pinned | bool | ChromeTab.Pinned | Mapped |
| 6 | extension_app_id | string | ChromeTab.ExtensionAppId | Mapped |
| 7 | navigation | repeated TabNavigation | ChromeTab.NavigationHistory | Mapped |
| 13 | browser_type | BrowserType | — | Not mapped (tab-level, redundant with window) |
| 14 | last_active_time_unix_epoch_millis | int64 | ChromeTab.LastActiveTime | Mapped |

Deprecated fields (skipped): favicon (8), favicon_type (9), favicon_source (11), variation_id (12)

## TabNavigation

| # | Field | Type | Maps to | Status |
|---|-------|------|---------|--------|
| 2 | virtual_url | string | NavigationEntry.Url | Mapped |
| 3 | referrer | string | NavigationEntry.ReferrerUrl | Mapped |
| 4 | title | string | NavigationEntry.Title | Mapped |
| 6 | page_transition | PageTransition | NavigationEntry.TransitionType | Mapped |
| 7 | redirect_type | PageTransitionRedirectType | — | Not mapped yet |
| 8 | unique_id | int32 | — | Skipped (internal) |
| 9 | timestamp_msec | int64 | NavigationEntry.Timestamp | Mapped |
| 10 | navigation_forward_back | bool | — | Not mapped yet |
| 11 | navigation_from_address_bar | bool | — | Not mapped yet |
| 12 | navigation_home_page | bool | — | Not mapped yet |
| 15 | global_id | int64 | — | Skipped (internal sync ID) |
| 17 | favicon_url | string | — | Not mapped yet (no field in NavigationEntry) |
| 20 | http_status_code | int32 | NavigationEntry.HttpStatusCode | Mapped |
| 25 | correct_referrer_policy | int32 | — | Not mapped yet |
| 26 | password_state | PasswordState | — | Skipped |

### PageTransition enum (relevant values)

| Value | Meaning |
|-------|---------|
| 0 | LINK |
| 1 | TYPED |
| 2 | AUTO_BOOKMARK |
| 5 | GENERATED |
| 6 | AUTO_TOPLEVEL |
| 7 | FORM_SUBMIT |
| 8 | RELOAD |
| 9 | KEYWORD |
| 10 | KEYWORD_GENERATED |
