# Changelog

## Unreleased

- Moved the new-tab page into the trusted content WebView as `brezza://newtab`, migrated legacy empty session URLs, and removed the Chrome-side start-page and address-suggestion overlay implementations.
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
