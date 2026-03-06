# Tab Machine Feature Plan

## Status
- Committed on main (`3f8b03c`): TabTrackingService + SyncTabNodeId plumbing
- Next: create `feature/tabmachine` branch and implement the new architecture
- Plan file: `C:\Users\Joe\.claude\plans\mighty-herding-bear.md`

## Architecture
- **FullSnapshots.db** (renamed from tabhistorian.db) — existing snapshot system, unchanged, pruning stays
- **TabMachine.db** — new event-sourced database, completely separate
- Two features are independent — separate DBs, APIs, UI views

## TabMachine.db Schema
3 tables: `tab_identities`, `tab_events` (with `state_delta` JSON column), `tab_current_state`
- state_delta: full state JSON on Opened, only changed fields on updates (delta compression)
- tab_current_state: latest known state per tab (for diffing, avoids reading FullSnapshots.db)
- Event types: Opened, Closed, Navigated, TitleChanged, Pinned, Unpinned, Updated

## Key Design Decisions
- TabMachineService takes in-memory snapshot data (List<ChromeWindow>), not snapshot IDs
- Matching runs against tab_current_state, not previous snapshot
- Worker: always reads Chrome + runs Tab Machine; full snapshot only if ≥30 min since last
- SnapshotService split: ReadCurrentState() (always) vs SaveSnapshot() (conditional)
- StorageService migration v3: remove tracking tables from FullSnapshots.db

## What to Keep from Current Code
- ChromeTab.SyncTabNodeId, SyncedSessionReader passing it through — keep
- StorageService migration v2 (sync_tab_node_id column) — keep
- TabTrackingService matching logic (Pass 0-3, MatchByPredicate) — copy into TabMachineService
- TabEvent.cs, TabIdentity.cs models — rework (add Updated type, state_delta)
- SnapshotService TabTracking dependency, Program.cs DI, Worker.cs — replace

## Phase 2 (Frontend)
- Tab Machine as main view at `/`, snapshots moved to `/snapshots`
- shadcn/ui: Slider + Calendar + Popover for time travel
- API: /api/tabmachine/search, /events, /timeline, /stats
- TabHistorian.Common gets TabMachineDb.cs (read-only access for web app)
