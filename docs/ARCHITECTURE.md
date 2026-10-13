# Architecture

## System Shape

The browser separates durable browser state and policy (Core), the native shell (App), the WebView2 runtime adapter, and local storage. The shell is a native Win32 window whose chrome is standard HWND controls; WebView2 renders only website content, parented to the browser window's HWND.

```mermaid
flowchart TD
    App["Brezza.App<br/>native Win32 shell, controls, message loop"]
    Core["Brezza.Core<br/>browser state and policies"]
    WV["Brezza.WebView2<br/>WebView2 controllers in HWNDs"]
    Store["Brezza.Persistence<br/>local storage"]
    Panel["Native panel dialog<br/>settings / history / bookmarks"]
    Engine["WebView2 Runtime<br/>untrusted page processes"]
    App --> Core
    App --> WV
    App --> Store
    App --> Panel
    WV --> Core
    Store --> Core
    WV --> Engine
    Engine -->|"validated page events"| WV
    WV --> App
```

## Project Responsibilities

| Project | Owns | Must not own |
|---|---|---|
| `Brezza.App` (`net48`) | Native window and controls, message loop, control layout and DPI, localization, native panel dialogs, composition root | WebView2 COM calls, browser state ownership, a second authoritative copy of browser state |
| `Brezza.Core` (`netstandard2.0`) | Tab registry/runtime lifecycle, navigation parsing, search providers, sleep policy, cleanup, download naming, permission policy | Win32, WPF, WebView2 SDK, filesystem access |
| `Brezza.WebView2` (`net48`) | Isolated environments, `CoreWebView2Controller` lifetime and event translation, private profiles and cleanup I/O | Search policy, tab ordering, persistence |
| `Brezza.Persistence` (`netstandard2.0`) | Versioned settings, history, bookmark, and session stores | UI or WebView2 dependencies |
| `Brezza.Core.Tests` | Deterministic tests of state transitions and policy | WebView2 Runtime behavior |

## Dependency Rules

Allowed project references are `App → Core`, `App → WebView2`, `App → Persistence`, `WebView2 → Core`, and `Persistence → Core`. Core has no project references. The published app targets .NET Framework 4.8; test code runs on .NET 10 against the `netstandard2.0` Core. No UI framework package is referenced: the shell uses Win32 through P/Invoke. Do not add a service locator, mediator, or dependency injection framework before a concrete need exists.

## Native Shell

`Program` starts a single `[STAThread]` message loop. `MessageLoopSynchronizationContext` owns a message-only window and marshals `SynchronizationContext` work onto the UI thread, so every `await` continuation in the shell runs where HWNDs and WebView2 controllers were created. The loop also inspects `WM_KEYDOWN`/`WM_SYSKEYDOWN` before dispatch, which is how browser shortcuts work while the WebView2 surface has focus.

`MainWindow` registers the `BrezzaMainWindow` class and creates the chrome as child controls:

| Control | Class | Purpose |
|---|---|---|
| Tab strip | `SysTabControl32` | Tab titles, selection, tooltips for clipped titles |
| New tab / new private tab | `BUTTON` | Create tabs |
| Back / forward / reload | `BUTTON` | History navigation |
| Address and search | `COMBOBOX` (drop-down) | Raw input submission; drop-down lists history and open tabs |
| Bookmark / history / bookmarks / settings | `BUTTON` | Toggle a bookmark, open a browser panel |
| Notice area | `STATIC` + `BUTTON` | Failure, download, permission, and cleanup notices with optional reload and dismiss |

Layout is computed in physical pixels from the window DPI. `WM_SIZE` repositions the controls and updates the active controller's `Bounds`; `WM_DPICHANGED` applies the suggested rectangle, rebuilds the fonts, and updates `RasterizationScale`. The app manifest declares `PerMonitorV2` awareness and a Common-Controls 6 dependency, so the controls follow the Windows visual style.

The tab strip is subclassed (`SetWindowSubclass`) so middle-click closes a tab and the wheel scrolls the strip; the address field's edit child is subclassed so Enter submits and Escape reverts. `TCN_SELCHANGE` activates the tab the user clicked; `TCM_SETCURSEL` only reflects Core's active tab, because a tab control does not notify for programmatic selection.

## WebView2 Hosting

`WebView2TabHost` owns one `CoreWebView2Controller` per tab, created with `CreateCoreWebView2ControllerAsync` against the browser window's HWND. Controller options are always supplied; private tabs set `IsInPrivateModeEnabled`. The host configures the surface so remote content cannot reach the browser:

- `IsWebMessageEnabled = false` and no `WebMessageReceived` handler is registered.
- `AreBrowserAcceleratorKeysEnabled = false`, so the native window owns shortcuts.
- Default context menus stay enabled for the page; the runtime's own download dialog is suppressed.

`ITabHost` exposes the view surface the shell needs without exposing COM objects: `SetVisible`, `SetBounds`, `SetRasterizationScale`, and `Focus`. Only the active tab's host is visible. Controller creation failures complete the host's creation task with the exception, so a tab that cannot render reports a failure instead of waiting forever. Closing a tab while its controller is still being created closes the controller as soon as it exists.

## Browser-Owned Views

Settings, history, and bookmarks are native panels in one owned dialog (`BrezzaInternalPanel`) built from a combo box, an edit field, a list box, and buttons. The panel reports intent (save settings, open a URL, remove an entry, clear history) and renders the data it is given.

The canonical `brezza://settings`, `brezza://history`, and `brezza://bookmarks` URLs remain the logical tab URLs, so the address field and session records keep working:

| Browser URL | Native view | Owner |
|---|---|---|
| `brezza://settings` | settings panel (search provider, language) | `Brezza.App` |
| `brezza://history` | history panel (filter, open, remove, clear) | `Brezza.App` |
| `brezza://bookmarks` | bookmarks panel (filter, open, remove) | `Brezza.App` |

A toolbar-opened panel is a window-level view state and closes when the user navigates or switches tabs. A tab whose URL is an internal route owns its panel; closing the panel returns that tab to the new-tab state (`TabRuntimeManager.ResetToNewTabAsync`) and entering the route releases its content host (`TabRuntimeManager.ReleaseHostAsync`), so a browser-owned page never holds a WebView2 surface.

## State Ownership

- `TabRegistry` is the source of truth for tab identity, ordering, and the active tab.
- A tab model is durable browser state. A WebView2 host is a disposable runtime resource and is never stored in a view model. An empty URL is the lightweight new-tab state and has no content host until navigation begins.
- `TabRuntimeManager` is the single owner of host creation, activation, sleep, and closure. It shares an in-flight host creation task per tab and closes late hosts after tab removal or shutdown.
- Implemented lifecycle is `Dormant → Creating → Active`, with `Sleeping`, failure, and closing paths. Empty-url and internal-route tabs are rendered by the native shell; inactive hosts are eligible for bounded sleep.
- Sleep requires eligibility: `TabSleepPolicy` releases an inactive host only when it is not loading, not playing audio, and not showing a full-screen element. The sweep keeps the eight-host budget and ten-minute inactivity threshold, and skips ineligible candidates. Reactivation reloads the saved URL; DOM and form state are not preserved when a host is released.
- App views render snapshots and send commands through app use cases. They do not mutate core collections or call WebView2 directly.

## Navigation Flow

The address field submits raw input to `NavigationInputParser`. It returns an empty target, a validated HTTP/HTTPS/file URL, or a search URL built from the configured provider. Search terms are encoded as one query component; typing does not trigger navigation or a provider request. Provider presets (Bing, Google, DuckDuckGo, Baidu) are owned by Core (`SearchProviderPresets`) so the native settings panel and persistence share one definition.

## Localization

The native shell reads `BrowserUi/locales/en.json` or `BrowserUi/locales/zh-Hans.json` and applies every label, button text, notice, tab fallback, and panel string from those packs. `BrowserSettings.Language` stores `""` for system default or an explicit supported language; the shell maps the Windows UI culture to `en` or `zh-Hans` and sends the effective language with each state snapshot. Changing language in Settings writes the preference and re-applies the native labels without changing browser state.

## Trust Boundaries

- Remote pages are untrusted. A content host starts with the message bridge disabled and never registers a message handler, so no page can address the browser even though the page messaging object exists in the document. There is no browser-owned HTTP origin: the `brezza.local` virtual-host mapping is gone, so a page that tries to read browser assets or navigate into a browser-owned page gets an ordinary network failure.
- Only the browser opens its own views. Internal routes are resolved by the shell from the tab's logical URL; a remote document cannot navigate the shell into one, and no browser-owned document is ever served to a page.
- WebView2 process failure is reported by the adapter; automated process recovery is not implemented yet.
- Normal tabs share `%LOCALAPPDATA%\Brezza\WebView2`. Private tabs share a separate per-window `PrivateSessions/{guid}` profile created with WebView2 private mode. The last private tab closes its controllers before the profile directory is removed; application shutdown also closes and cleans the private profile.

## Browser-Owned Requests

Downloads, popups, and permission requests are decided by Core and translated by the adapter:

| Request | Core contract | App behavior |
|---|---|---|
| Download | `BrowserDownloadPaths` sanitizes and de-duplicates the file name using an adapter-supplied existence check; Core performs no filesystem I/O | Downloads go to the configured folder with start/completion/failure notices; the runtime's own download dialog is suppressed |
| Popup | `PopupRequested` carries the requested destination and originating session kind | HTTP/HTTPS destinations open in a tab of the same session; blank requests open an empty tab; other schemes are ignored |
| Permission | `BrowserPermissionPolicy` grants nothing without a prompt | The request is denied and reported through a notice |

## Private Session Cleanup

`ITabHostFactory.CleanupSessionAsync` returns a `SessionCleanupResult` with `Completed`, `Pending`, or `Failed`, the data folder, the number of deletion attempts, and the last failure detail. `Completed` requires verified removal. A folder that is not verified removed stays tracked by the factory and by `TabRuntimeManager`, which exposes `PendingPrivateCleanup`, `RetryPendingPrivateCleanupAsync`, and the `PrivateCleanupCompleted` event. Cleanup attempts are serialized; timer retries run only while no private tabs are open. The shell shows a notice while removal is pending or failed, and shutdown makes a final attempt after closing hosts.

## Persistence

`SettingsStore`, `HistoryStore`, `BookmarkStore`, and `SessionStore` write schema-versioned XML through a temporary file and atomic replacement; corrupt or unsupported files fall back to safe defaults. Settings schema version 2 adds a `theme` value (`System`, `Light`, or `Dark`) and accepts schema 1 files with the system default. History is recorded after successful normal HTTP/HTTPS navigation and flushed at shutdown. Private tabs are excluded from history and saved sessions. Session restore preserves normal tab identity, order, duplicate URLs, and the active tab; legacy `brezza://newtab` records migrate to the empty new-tab URL, and the store accepts previously opened HTTP/HTTPS, internal, and local file URLs. The default root is `%LOCALAPPDATA%\Brezza` (ADR 0006).

## Release

`tools/package.ps1` publishes `net48` as an x64 framework-dependent ZIP named for the native shell. It verifies required assemblies and the language packs, checks that `WebView2Loader.dll` matches the x64 RID, removes PDB files, the RID-specific native folder, and the unused WPF/WinForms WebView2 assemblies, then starts the extracted application with an isolated data root to verify its native window, controls, and WebView2 content surface. Users require Windows/.NET Framework 4.8 and the Evergreen WebView2 Runtime; the .NET 10 SDK and reference-assembly package are build-only dependencies.

## Verification

Core tests cover URL/search classification, provider presets, persistence recovery, sleep eligibility, host release, observable session cleanup with retry, concurrent activation, private-session cleanup triggering, close-during-create, and shutdown-during-create. `tools/windows-smoke/smoke.ps1` drives the real native controls in a Debug build and covers the new-tab page, native panels for settings/history/bookmarks, page navigation, internal routes as native panels, the remote-content boundary (no bridge, no browser assets, no browser-owned page), normal/private tab operations through the native tab strip, private-profile cleanup, failure feedback, and session restore across a second process. `tools/package.ps1` publishes an x64 framework-dependent bundle and smoke-tests it with the installed WebView2 Runtime.
