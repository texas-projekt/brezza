# ADR 0010: Toolbar views over internal routes

- Status: Accepted
- Date: 2026-10-15

## Context

The former HTML internal pages (`brezza://settings`, `brezza://history`, `brezza://bookmarks`) were
replaced by native panels, but they kept living as logical tab URLs. The shell resolved the route
from the active tab's URL, released that tab's content host when it entered the route, and returned
the tab to the new-tab page when its panel closed. A tab therefore *owned* a panel, and a panel
opened from the toolbar needed a flag so the tab's own panel did not take the window back the moment
it opened.

The mechanism cost more than it returned:

- The URL was inert. It could not be bookmarked, shared, or reloaded into anything meaningful, and
  the address field showed a `brezza://` string that no longer navigated anywhere.
- Session records had to keep accepting internal URLs, and `TabRuntimeManager` carried
  `ResetToNewTabAsync` and `ReleaseHostAsync` only so a tab could enter and leave a route.
- Restoring a session never opens a panel — a panel is not a page — so the route's only durable
  effect was a tab that reopened as an empty new tab anyway.

Meanwhile the browsing-data tools were still shaped like the old toolbar actions: a modal
confirmation that cleared all cookies, and a panel that measured the profile folder.

## Decision

All six panels are toolbar views with no browser URL of their own. They open from the toolbar menu
— history (Ctrl+H), downloads (Ctrl+J), bookmarks, cookies, browsing data (Ctrl+Shift+Delete), and
settings — and close when the user navigates, switches tabs, presses Escape, or uses the panel's own
close action.

The `brezza` scheme is no longer navigable. `NavigationInputParser` accepts HTTP, HTTPS, and file
URLs only, so a `brezza://` address is searched like any other text that is not an address, and
`IsInternalPage`/`GetCanonicalInternalUrl` are removed. Saved sessions migrate once: a tab whose URL
starts with `brezza://` reopens as an empty new-tab page, which covers both the legacy
`brezza://newtab` record and the former routes. The tab is kept rather than dropped, so the user's
tab count and order survive the migration.

Cookies and browsing data became table panels in the same shape as history and bookmarks, instead of
toolbar actions with dialogs:

- The cookies panel is a flat, searchable list of the profile's cookies. A row shows the cookie's
  name over where it lives, when it ends, and the flags the site set; the row's own button deletes
  that one cookie without asking, and the footer action clears them all behind a confirmation.
- The browsing-data panel lists the four categories the user can clear — cached files, website
  storage, service workers, and cookies — each with what it covers, a per-row clear behind a
  confirmation, and a clear-all footer.

The panel shows no size summary. WebView2 exposes no per-category size or enumeration, and a
measured profile folder is mostly runtime data (component caches, crash reporting, hyphenation
data) that no clear touches, so the number would not move when the user cleared everything.

## Consequences

- `TabRuntimeManager` loses `ResetToNewTabAsync` and `ReleaseHostAsync`; the shell loses the
  panel-ownership flag and the internal-route branch in navigation and state rendering. A
  browser-owned view never has a WebView2 surface to create or release.
- A `brezza://` address typed into the address field is a search. The Windows smoke asserts that no
  panel opens and that the tab's URL is the search result.
- Cookie and browsing-data clearing share one adapter contract (`IBrowserDataManager` with
  `GetCookiesAsync`, `RemoveCookieAsync`, and `ClearAsync(BrowserBrowsingDataKind)`), so a category
  clear and a cookie clear reach the profile the same way and report the same observable result.
- A single cookie deletion is applied by the runtime after the call returns, so the adapter reads
  the store back until the cookie is gone; otherwise a row the user removed could still be listed.
- The smoke's isolated profile cannot hold cookies for a `file://` page, so the harness serves a
  local HTTP origin that plants them and also acts as the search provider, which keeps the whole
  run off the public network.
