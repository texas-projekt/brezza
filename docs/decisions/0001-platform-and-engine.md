# ADR 0001: Windows UI and Web Engine

- Status: Accepted
- Date: 2026-10-07

## Context

Brezza targets Windows and needs an embedded browser engine with a supported Windows integration, independent browser state, and a small native application shell. The reference product's WebKit choice is not itself a user-facing requirement. Keeping the app small also means avoiding a separately installed modern .NET Desktop Runtime or bundling that runtime with the app.

## Decision

Use WPF on .NET Framework 4.8 for the top-level window and WebView2 for trusted browser chrome and web content. Target `netstandard2.0` for Core and Persistence so they remain testable from a modern .NET test host. Keep WebView2 behind `Brezza.WebView2`. Require the Evergreen WebView2 Runtime and detect its absence at startup; do not bundle the browser engine. Browser chrome isolation is specified in [ADR 0003](0003-webview2-rendered-chrome.md).

## Consequences

- Trusted chrome and page content use separate WebView2 surfaces and user-data folders; the page-content surface has no message bridge.
- Standard Windows 10/11 installations already include .NET Framework 4.8 or a compatible in-place update, avoiding an additional app runtime download for the supported OS baseline.
- Core logic can be tested on .NET 10 without WPF or a browser runtime.
- The app depends on WebView2 Runtime availability and must handle a missing runtime at startup with a clear install path.
- Developers need the .NET 10 SDK and restoreable .NET Framework reference assemblies to build; these are development dependencies, not app runtime payload.
- A different engine would require a new adapter and separate compatibility evaluation; it is not promised by the current interface.
- Windows is the supported target for the first release; cross-platform UI is not a goal.
