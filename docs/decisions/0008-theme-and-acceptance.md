# ADR 0008: Native theme preference and acceptance boundary

## Context

The native shell needs a predictable appearance on light and dark Windows desktops while keeping theme state out of browser tabs and web content. Public-web and visual acceptance also need to be reported separately from deterministic tests when no CUA connector is present.

## Decision

Persist a versioned `BrowserThemeMode` setting with `System`, `Light`, and `Dark` values in the existing `%LOCALAPPDATA%\\Brezza` settings file. The shell resolves `System` from the Windows app light-theme registry preference and applies one palette to the main window and native panels. WebView2 content remains page-controlled; changing the chrome theme does not alter history, bookmarks, tabs, or session data.

CUA acceptance is a separate delivery gate. A Windows UI Automation or Win32 smoke run may verify controls and lifecycle, but it must be labeled as an automation substitute until a CUA connector produces screenshots and logs for the same scenarios.

## Consequences

Settings schema 1 files remain readable and default to `System`. Native controls receive theme colors through `WM_CTLCOLOR*`; the implementation does not add a UI framework or a remote page bridge. Real public-web results remain environment-dependent and are tracked as M7 evidence.
