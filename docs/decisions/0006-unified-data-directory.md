# ADR 0006: Unified local data directory

- Status: Accepted
- Date: 2026-10-12
- Supersedes: ADR 0002's separate data-directory decision

## Context

The earlier prototype and application used separate data directories. The user requested removal of existing browser data and a single data directory named for Brezza.

## Decision

Use `%LOCALAPPDATA%\Brezza` as the default root for settings, history, bookmarks, session records, trusted chrome, and content profiles. Reset the existing local data at the user's request. The application creates the new root when it next starts.

Retain `BREZZA_DATA_DIR` as an explicit override for isolated development, smoke tests, and packaging checks. Data is not migrated or backed up as part of this reset.

## Consequences

All browser data follows one default root. The app starts from defaults following the reset. WebView2 profile isolation, persistence schemas, and session ownership are unchanged.
