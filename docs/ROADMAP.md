# Roadmap

Milestones are accepted only when their exit criteria pass. Status describes verified delivery, not estimated progress.

| Milestone | Status | Exit criteria |
|---|---|---|
| M0 Foundation | Complete | `net48` app and WebView2 adapter plus `netstandard2.0` Core/Persistence build; missing Runtime is reported; installed Runtime startup is smoke-tested. |
| M1 Search and navigation | Complete | URL/search classification, configurable persisted search provider, current-tab navigation, browser-owned new-tab page, and useful navigation failure states. |
| M2 Tabs and lifecycle | Complete | Create/switch/close tabs; empty new tabs avoid content hosts; background lazy host creation; bounded inactive-host sleep; concurrent activation is deduplicated; shutdown closes hosts deterministically. |
| M3 Local browser data | Complete | Versioned settings, history, bookmarks, normal session restore, corrupt-file recovery. |
| M4 Privacy and release readiness | Complete | Private profile isolation/exclusion/cleanup, keyboard shortcuts, and an extracted x64 ZIP startup smoke are verified. |
| M5 Native Win32 shell | Complete | The browser window, tab strip, address/search field, navigation buttons, notices, and settings/history/bookmark panels are native HWND controls; WebView2 renders only website content; Core and Persistence keep their responsibilities; deterministic Core tests and the native-control Windows smoke pass; the extracted x64 package starts with native controls and a WebView2 surface. |
| M6 UI productization | In progress | Theme selection (System/Light/Dark) is persisted and applied to the native shell and panels; DPI-safe toolbar layout, address input behavior, tab-strip scrolling/close gestures, and per-tab favicons are verified; the cookies panel's list, filter, per-row delete, and confirmed clear-all, and the browsing-data panel's four categories with per-category and clear-all actions through the menu command and Ctrl+Shift+Delete, are verified; the downloads panel's list, filter, row actions, and confirmed clear-all are verified against a real download, with the list restored in a later process. |
| M7 Real web compatibility | Planned | Repeatable WebView2 pages and a documented public-web matrix cover navigation failures, downloads, popups, permissions, media, and private-session boundaries. |
| M8 CUA acceptance | Blocked by environment | CUA screenshots and logs cover supported DPI/theme combinations and the M6/M7 workflows; until a CUA connector is available, Windows UI Automation smoke remains explicitly labeled as a substitute. |

Changes to product scope or a milestone exit criterion require an ADR or a product update before implementation begins.

## Browser host follow-up

ADR 0005 adds per-page command boundaries, observable private cleanup, media-aware tab release, and basic download/popup/permission handling. These changes extend the existing browser without introducing a separate product track. Core regression checks and Windows smoke cover the existing navigation, session, and remote-content boundaries. Real Runtime coverage now drives a download end to end (the file is written, recorded, listed, and restored); popup user gestures, permission approval UI, and clean-machine validation remain follow-up work; they are not claimed by the completed M0–M5 criteria.

## Native shell follow-up

The native shell covers the product's workflows, and a few chrome details are simpler than the HTML chrome it replaced:

- The tab strip has no per-tab close button; tabs close with Ctrl+W, middle-click, the Delete key, or the active tab's close button, which stays reachable however many tabs share the strip. Tabs stop shrinking at the minimum width and the strip scrolls sideways past that, following the active tab with no scroll buttons: a thin indicator under the tabs says the strip scrolls, its thumb showing how much of the strip is visible and where the view sits. The new-tab button stops moving once the tabs have filled the band. The strip shows tooltips for clipped titles.
- The address field offers a drop-down list of history and open tabs instead of an inline suggestion overlay, and has no connection indicator icon.
- Tabs show each page's favicon ahead of its title, fetched through the tab's own WebView2 host so the request stays inside the page's profile; Core tracks the favicon URI, the shell decodes and caches the image per tab so it survives a sleeping host, and a tab without a favicon leads with the built-in globe glyph from the same icon font. A tab's label is always painted and ellipsized as the tab narrows, and the minimum tab width is derived from what a tab must still show: its leading icon, the room for that ellipsis, and the active tab's close button. A private tab is marked by its private glyph alone, and keeps the page's own title.
- WebView2 zoom, find-in-page, and printing are not surfaced in the native UI.
