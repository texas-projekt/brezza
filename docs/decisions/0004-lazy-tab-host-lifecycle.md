# ADR 0004: Lazy Tab Host Lifecycle

- Status: Accepted
- Date: 2026-10-11

## Context

WebView2 host creation and navigation are materially more expensive than switching a browser tab descriptor. The previous implementation created a content host for the empty new-tab state and retained every created host indefinitely. This exposed Chromium startup cost to new-tab actions and allowed memory use to grow with every tab.

## Decision

Keep `BrowserTab` as logical state only. Empty URL is the lightweight new-tab state rendered by trusted Chrome. Content hosts are created only when a tab has a navigable URL, initialized in the background for user actions, and retired when a tab is closed or enters the configured sleep policy. Session restore stores logical URLs only; legacy `brezza://newtab` values migrate to empty URLs.

The runtime manager owns host creation, navigation observation, host retirement, and sleep candidates. Generation checks prevent stale background navigation from changing current tab state. Browser-owned settings, history, and bookmark views use trusted same-origin frames in the Chrome surface; they do not navigate content tabs or allocate content hosts.

## Consequences

- New tabs respond without creating a content WebView2.
- A sleeping tab restores by creating a new host and navigating to its saved URL; transient page state may be lost.
- Core remains independent of WebView2 and persistence.
- Host sleep is bounded by eight live hosts and a ten-minute inactive threshold; the active tab is never slept.
