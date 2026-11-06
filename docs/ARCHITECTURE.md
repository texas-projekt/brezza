# Architecture

For people changing Brezza: the parts, the rules that keep them apart, and why. Building and testing
are in the [README](../README.md#development).

## Projects

| Project | Target | Owns | Must not own |
|---|---|---|---|
| `Brezza.App` | `net48` x64 | Window and controls, message loop, layout, DPI, theme, localization, panels, composition root (`BrowserShell`) | WebView2 calls, a second copy of browser state |
| `Brezza.Core` | `netstandard2.0` | Tabs and host lifecycle, input parsing, search presets, and the sleep, popup, permission, download, navigation-failure, and browsing-data policies | Win32, the WebView2 SDK, file access |
| `Brezza.WebView2` | `net48` | One controller per tab, event translation, profiles, private-profile cleanup, cookie and data access | Search, tab order, persistence |
| `Brezza.Persistence` | `netstandard2.0` | Versioned XML stores for settings, history, bookmarks, session, downloads | UI or WebView2 |

References go App → Core, WebView2, Persistence; WebView2 → Core; Persistence → Core. Core references
nothing. There is no UI framework, DI container, or mediator.

Large types are partial classes split by concern: `BrowserShell` (`.Panels`, `.Downloads`, Debug-only
`.Smoke`), `MainWindow` (`.Layout`, `.TabStrip`, `.Input`), `InternalPanelWindow` (`.Settings`,
`.List`, `.Layout`). `ShellIntents.cs` holds the small types the window and the shell exchange.

## Invariants

**State.** `TabRegistry` owns tabs and the active tab; `DownloadRegistry` owns the download list. The
UI renders snapshots and sends commands. `TabRuntimeManager` alone creates, activates, sleeps, and
closes hosts; concurrent activation shares one creation, and a host that finishes after its tab
closed is closed at once.

**Hosts.** A tab with no URL has no host. At most eight inactive hosts stay alive; an inactive host
sleeps after ten minutes unless it is loading, playing audio, full-screen, or downloading. Waking
reloads the saved URL.

**The page boundary.** `ITabHost` is the only view of a page the shell gets: state, commands
(`NavigateAsync`, `Reload`, `Stop`, download controls), and events. No COM object crosses it. Content
hosts run with the message bridge off, no message handler, and browser accelerator keys off. External
schemes other than user-initiated `mailto`, `tel`, and `sms` are cancelled. Popups need a user gesture
and are rate-limited per tab and per minute; they open as tabs. Permission requests are denied and
reported.

**Navigation outcomes.** A failure older than the tab's newest navigation id is ignored. A load that
was replaced, taken over by a download, blocked as an external scheme, or stopped by the user is not
a failure; a stopped load returns the tab's address to the page still shown. An HTTP error status is
the site's answer, not a failure. A real failure is classified by `NavigationFailurePolicy`.

**Private sessions.** Private tabs share one profile under `PrivateSessions/{guid}` and never reach
history, the session, or the stored download list. Cleanup reports `Completed` only after verified
removal; otherwise it retries on a timer while no private tab is open, and an `owner.txt` marker lets
the next start remove folders whose process is gone.

**Persistence.** Stores write through a temporary file and an atomic replace. An unreadable file is
renamed `*.corrupt-<ticks>`, never overwritten; one bad field falls back alone. A failed write raises a
notice. Only finished downloads of normal sessions are written.

**Native shell.** One message loop; `MessageLoopSynchronizationContext` returns every `await` to the UI
thread. No window procedure lets an exception reach the OS. Every async command from a native event
goes through `BrowserShell.Observe`. A session-local mutex keeps one window per user. Colors live
only in `NativeThemePalette` and sizes only in `NativeMetrics`.

## Decisions

Add an entry before adding a package or framework or changing a rule above.

| Decision | Why | Cost |
|---|---|---|
| Native Win32 chrome through P/Invoke; WebView2 renders pages only (replaced HTML chrome, 2026-10) | Small shell, and no browser-owned origin or bridge for a page to reach | Hand-made layout and painting; no per-row accessibility in panels |
| `net48` app on the Evergreen WebView2 Runtime; `netstandard2.0` Core | Nothing extra to install; Core testable on modern .NET | Windows only; needs the runtime present |
| Lazy hosts with a sleep budget | A host is a browser process; new tabs must be instant | A woken tab reloads and loses form and scroll state |
| Panels are toolbar views; `brezza://` is not navigable | A URL for chrome gave pages a way in and gave users nothing | Panels cannot be bookmarked or restored |
| One data root, `%LOCALAPPDATA%\Brezza` (`BREZZA_DATA_DIR` overrides) | All local data in one place | No migration from the prototype |
| Downloads folder is a setting, resolved per download | Changing it needs no restart | A paused download is lost if the browser closes |
| Release ZIP merged by a pinned ILRepack, not internalized | Fewer files; internalizing breaks WebView2 events | New assemblies must be added to the merge list |
| Tests are unit tests on `net48` plus a short smoke over the main paths (2026-11) | `net48` is what ships; behavior details belong in unit tests, the smoke only proves the wiring | No coverage gate, no screenshot baseline; UI details are checked by hand |
| Releases are unsigned; SHA256 and GitHub provenance instead | No certificate | Windows warns about an unknown publisher |
| `NavigationFailurePolicy` explains failures and decides Try again (2026-11) | Raw status names told users nothing | — |
| No search box on the new-tab page; a new tab focuses the address field (2026-11) | One search field is the product | — |
| Apache-2.0; the release ships the WebView2 SDK license (2026-11) | Permissive with a patent grant; the SDK license requires its notice | — |
| Three documents: README, this file, AGENTS (2026-11) | One maintainer; fewer files stay current | No changelog file; release notes are written by hand |
