# Changelog

- Added a persisted native chrome theme preference with System, Light, and Dark modes. The main window and native settings/history/bookmark panels share the resolved palette, and toolbar layout now keeps the address control within its row at high DPI. Settings schema 1 files continue to load with the System theme.
- Polished native interaction details: restored the address cue banner on the inner edit control, added a visible active-tab close button, added compact controls for the minimum window size, fixed dark notice and panel colors, and measured localized panel buttons with the actual UI font.
- Added explicit native empty-state controls for history/bookmarks and darkened the settings panel's combo boxes so localized panel states remain readable in dark mode.
- Added a loading marker to the active tab while WebView2 navigation is in progress, with a smoke assertion that the marker disappears after completion.
- Reapplied main-window localization after top-level HWND creation so startup labels are not lost during the Win32 `WM_CREATE` lifetime boundary.
- Fixed tab-strip mouse-wheel direction handling so wheel scrolling activates the tab Core tracks, including negative signed wheel deltas.

## Unreleased

- Replaced the HTML/WebView2 browser chrome with native Win32 UI (ADR 0007): the window, tab strip, address/search field, navigation buttons, notice area, and settings/history/bookmark panels are real HWND controls owned by `Brezza.App` through P/Invoke, hosted in an STA message loop with a `SynchronizationContext` that keeps continuations on the UI thread.
- Hosted WebView2 content with `CoreWebView2Controller` directly against the browser window's HWND, with per-monitor DPI awareness, DPI-scaled fonts and controller bounds, and explicit rasterization scale. Removed the WPF `WebView2` control, the chrome surface, the `brezza.local` virtual-host mapping, and the versioned message bridge.
- Kept the trust boundary structural: content hosts disable the message bridge and register no message handler, so a remote page cannot address the browser; there is no browser-owned HTTP origin for a page to read or navigate into.
- Migrated `brezza://settings`, `brezza://history`, and `brezza://bookmarks` to native panels while keeping the logical URLs in the address field and session records; a tab entering an internal route releases its content host, and closing the panel returns the tab to the new-tab page.
- Moved search-provider presets into Core so the native settings panel and persistence share one definition, and added Core tests for the presets, host release, and the active-host surface contract.
- Removed the obsolete command-protocol parser, trusted-origin policy, chrome message types, and their tests; `ITabHost` now exposes view-surface operations (`SetVisible`, `SetBounds`, `SetRasterizationScale`, `Focus`) instead of a script bridge.
- Updated the Windows smoke harness to drive the real native controls (button commands, address-field input, tab-strip clicks and middle-click) and to assert native control state, tab items, and labels; the removed ChromeProfile/JS-toolbar path is gone.
- Updated packaging to produce `Brezza-<version>-win-x64-native.zip`, verify the x64 loader, drop the unused WPF/WinForms WebView2 assemblies, and start the extracted package to verify its native window, controls, and WebView2 surface. The previous `Brezza-0.1.7-win-x64.zip` is preserved.
- Fixed WebView2 tab-host event detachment so every controller subscription is removed when a host closes, and made the Windows smoke wait for the whole navigation before asserting the active tab's loading marker.

## Earlier

- Unified the default local data directory under `%LOCALAPPDATA%\Brezza` after a user-requested reset of existing browser data.
- Restricted remote-page messaging and internal-page commands; fixed host-state delivery to standalone internal pages.
- Made private-profile cleanup results and retries observable, protected new private sessions from cleanup retries, and kept loading/audio/full-screen tabs awake while preserving the ten-minute inactivity window.
- Added basic download status notices, popup-to-tab handling that preserves the originating session, and explicit permission-denied notices.
- New-tab state is lightweight and rendered by trusted Chrome; content WebView2 hosts are created only after navigation.
- Added single-entry history removal and clear-all history management in the trusted history panel.
- Created the independent Brezza solution and documented its product scope, architecture, platform decision, and delivery milestones.
- Set the shipping WPF app and WebView2 adapter to .NET Framework 4.8, with Core and Persistence targeting .NET Standard 2.0.
- Implemented URL/search navigation, a persisted configurable provider, WebView2 startup detection, and working tabs with lazy shared-profile hosts.
- Added local history/open-tab suggestions, versioned browser data stores, lifecycle/bridge regression tests, and a passing isolated Windows WebView2 smoke test.
- Completed bookmark management and versioned normal-session save/restore; cold start opens a local search page and restores previously open tabs.
- Set browser chrome rendering to a dedicated trusted WebView2 surface, with a separate content WebView and origin-restricted command bridge.
- Added private tabs with a separate WebView2 private profile, normal-data exclusion, and last-tab profile cleanup; verified by the Windows smoke flow.
- Added an x64 framework-dependent packaging script and verified the extracted ZIP starts its WebView2 chrome.
- Added visible keyboard focus, accessible tab labels, suggestion arrow-key navigation, and a chrome keyboard smoke assertion.
- M4 release readiness passed: private profile cleanup, trusted chrome accessibility semantics, and extracted x64 package startup are verified.
- Added runtime i18n for English and Simplified Chinese, system-language fallback, persisted language selection, and localized chrome smoke coverage.
- Converted settings, history, and bookmarks into trusted `brezza.local` internal pages using the same page/bridge lifecycle.
- Added complete chrome i18n for English and Simplified Chinese, including system-language fallback and a persisted language setting.
- Redrew the browser chrome and internal pages in a flat style that follows the Windows light/dark theme, with a distinct private-browsing palette. Tabs update in place and support middle-click close, Delete, Home/End, and Ctrl+Tab. The address field shows a formatted URL with a connection indicator. Suggestions open as an overlay whose first row is the host-classified search or address. Notices can be dismissed or used to reload.
- Added the chrome-only `layout` command so the chrome reports its height and overlay state to the host, which hides the page while suggestions cover it. Added `removeBookmark` to the bookmarks page, filtering for history and bookmarks, and search-engine presets in settings. Closing an internal page with no history now returns the tab to the new-tab page instead of `about:blank`.
