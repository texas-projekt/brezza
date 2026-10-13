# ADR 0007: Native Win32 browser UI

- Status: Accepted
- Date: 2026-10-12
- Supersedes: ADR 0003's decision to render the browser chrome as HTML/CSS/JS in a WebView2 surface

## Context

The user instructed that Brezza must use native Win32 UI ("使用 Win 32 UI"). The previous shell rendered the tab strip, toolbar, address field, notices, and the settings/history/bookmarks pages as HTML in a trusted `brezza.local` WebView2 surface, with WPF as the top-level host. That design is no longer acceptable: the browser chrome must be native Windows controls, while WebView2 stays the rendering engine for website content.

The migration must keep the working Core and Persistence layers, must not introduce WPF, WinForms control abstractions, WinUI, Avalonia, or Electron for the replacement UI, and must not rewrite Core in C++ merely because the UI is Win32.

## Decision

Replace the HTML chrome with native Win32 UI hosted by a .NET Framework 4.8 application that uses real Win32 APIs through P/Invoke.

- `Brezza.App` registers its own window class (`BrezzaMainWindow`) and builds the shell from standard controls: a `SysTabControl32` tab strip, `BUTTON` navigation and panel buttons, an editable `COMBOBOX` address/search field with a cue banner, and a `STATIC` notice area. It owns its message loop and a `SynchronizationContext` that marshals `await` continuations to the UI thread. No WPF, WinForms, WinUI, Avalonia, or Electron control abstraction is used.
- `Brezza.WebView2` hosts page content with `CoreWebView2Controller` created directly against the browser window's HWND (`CreateCoreWebView2ControllerAsync`), positioned with `Bounds` and scaled with `RasterizationScale`. The WPF `WebView2` control, the chrome surface, the `brezza.local` virtual-host mapping, and the versioned message bridge are removed.
- The browser window is per-monitor DPI aware (`PerMonitorV2` in the app manifest) and depends on Common-Controls 6. Controls, fonts, and controller bounds are recomputed on `WM_DPICHANGED` and `WM_SIZE`.
- Settings, history, and bookmarks become one native dialog (`BrezzaInternalPanel`) with a combo box, an edit field, and a list box. The canonical `brezza://settings`, `brezza://history`, and `brezza://bookmarks` URLs remain the logical tab URLs, so session records and address-bar input keep working; the shell renders them as native views instead of WebView2 documents and releases the tab's content host (`TabRuntimeManager.ReleaseHostAsync`).
- Localization keeps the existing `BrowserUi/locales/{en,zh-Hans}.json` packs, read by the native shell; only the HTML/CSS/JS chrome assets are removed.
- Core and Persistence are unchanged in responsibility. `Core` keeps owning tab identity, ordering, navigation classification, sleep eligibility, private-session cleanup, download naming, and permission policy. The shell's `ITabHost` gains `SetVisible`, `SetBounds`, `SetRasterizationScale`, and `Focus` so the adapter expresses view concerns without the App touching WebView2 objects.

## Consequences

- The trust boundary becomes structural: there is no browser-owned HTTP origin, no virtual-host mapping, and no message bridge. Content hosts start with `IsWebMessageEnabled = false` and register no `WebMessageReceived` handler, so a remote page cannot address the browser even though the page messaging object still exists. Browser accelerators are disabled on content hosts so the native window handles them.
- Tabs follow the native control's semantics: `TCN_SELCHANGE` activates a tab when the user clicks the strip, and `TCM_SETCURSEL` only reflects Core state, because a tab control does not notify for programmatic selection. Middle-click closes a tab and the wheel scrolls the strip through a comctl32 subclass.
- Tab text is derived from the document title, the URL host, the file name, or the localized internal-page name; private tabs carry the localized private label. Tab width is capped so long titles do not squeeze the strip.
- Startup, navigation, process, and download failures surface in the native notice area, and a missing WebView2 Runtime is reported on the new-tab page instead of a modal dialog.
- The Windows smoke harness now drives the real native controls (button `WM_COMMAND`, address-field `WM_KEYDOWN`, tab-strip clicks and middle-click) and asserts native control state, tab items, and control labels; the removed `ChromeProfile`/JS-toolbar path is gone.
- `tools/package.ps1` verifies the x64 loader, removes the unused WPF/WinForms WebView2 assemblies, and starts the extracted package to assert the native window, its controls, and a live WebView2 surface.
- ADR 0005's host boundaries (deny-and-notice permissions, observable private cleanup, media-aware sleep, popups, downloads) are unchanged and still enforced by Core.
