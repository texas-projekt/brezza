# ADR 0003: WebView2-Rendered Browser Chrome

- Status: Superseded by [ADR 0007](0007-native-win32-ui.md) for browser chrome rendering
- Date: 2026-10-07

## Context

Brezza's browser chrome must be rendered by WebView2, following the useful multi-WebView pattern in the existing WebView2Browser project. The initial shell used native WPF controls, which did not match this product direction.

## Decision

Use a thin WPF top-level host with two WebView2 surfaces: a trusted local chrome surface and an isolated active-tab surface. Load `BrowserUi` assets through the `brezza.local` virtual host mapping. Keep the chrome and content user-data folders separate. The active-tab surface normally renders remote content; it may render a trusted internal page when the logical URL is a `brezza://` route. Enable the versioned message bridge only for the exact trusted origin on either surface. Private tabs use a third isolated profile directory and `IsInPrivateModeEnabled`; the profile is deleted after the final private tab closes.

## Consequences

- Tabs, address/search input, browser-owned internal pages, and browser commands use HTML/CSS/JS while WPF owns window lifetime and surface placement.
- The new-tab, settings, history, and bookmarks pages are real pages in the current tab, with canonical `brezza://` URLs. `https://brezza.local/*.html` is only the WebView2 resource mapping.
- Remote pages cannot invoke browser commands through WebView2 messaging.
- Core and persistence logic remain independent of the rendering choice.
- The C++ WebView2Browser host and its numeric command protocol are not copied; only the isolated-surface and message-boundary lessons are reused.
- Chrome UI tests must cover protocol validation in addition to Core behavior.
