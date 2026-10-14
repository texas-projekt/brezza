# ADR 0008: Native theme preference and acceptance boundary

- Status: Accepted
- Date: 2026-10-13

## Context

The native shell needs a predictable appearance on light and dark Windows desktops while keeping theme state out of browser tabs and web content. Public-web and visual acceptance also need to be reported separately from deterministic tests when no CUA connector is present.

## Decision

Persist a versioned `BrowserThemeMode` setting with `System`, `Light`, and `Dark` values in the existing `%LOCALAPPDATA%\Brezza` settings file. The shell resolves `System` from the Windows app light-theme registry preference and applies one palette to the main window and native panels.

A theme is color data only: a `NativeThemePalette` instance with named color roles and a light or dark base, which selects the Windows rendering of standard controls. Sizes, spacing, and radii are shared tokens in `NativeMetrics`, outside the theme, so a theme cannot change layout. The shell owns one `NativeThemeSource`; every native window reads its palette there and repaints on its `Changed` event, so no window keeps its own copy of the theme. WebView2 content remains page-controlled; the chrome's light/dark scheme is passed to content as the CSS `prefers-color-scheme` preference through the WebView2 profile's `PreferredColorScheme`, and pages that define their own colors keep them. The default surface color behind an unpainted page follows the chrome palette, and the preference is per profile, so all tabs of a session share it. Changing the chrome theme does not alter history, bookmarks, tabs, or session data.

CUA acceptance is a separate delivery gate. A Windows UI Automation or Win32 smoke run may verify controls and lifecycle, but it must be labeled as an automation substitute until a CUA connector produces screenshots and logs for the same scenarios.

## Consequences

Settings schema 1 files remain readable and default to `System`. Native controls receive theme colors through `WM_CTLCOLOR*`; the implementation does not add a UI framework or a remote page bridge. Real public-web results remain environment-dependent and are tracked as M7 evidence. Built-in WebView2 pages and form controls follow the chrome scheme through `prefers-color-scheme`; a site that ignores that media feature looks unchanged.
