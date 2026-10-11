# Roadmap

Milestones are accepted only when their exit criteria pass. Status describes verified delivery, not estimated progress.

| Milestone | Status | Exit criteria |
|---|---|---|
| M0 Foundation | Complete | `net48` app and WebView2 adapter plus `netstandard2.0` Core/Persistence build; missing Runtime is reported; installed Runtime startup is smoke-tested. |
| M1 Search and navigation | Complete | URL/search classification, configurable persisted search provider, current-tab navigation, browser-owned new-tab page, and useful navigation failure states. |
| M2 Tabs and lifecycle | Complete | Create/switch/close tabs; lazy host creation; concurrent activation is deduplicated; shutdown closes hosts deterministically. |
| M3 Local browser data | Complete | Versioned settings, history, bookmarks, normal session restore, corrupt-file recovery. |
| M4 Privacy and release readiness | Complete | Private profile isolation/exclusion/cleanup, keyboard shortcuts, trusted chrome accessibility semantics, and an extracted x64 ZIP startup smoke are verified. |

Changes to product scope or a milestone exit criterion require an ADR or a product update before implementation begins.
