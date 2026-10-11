# ADR 0002: Reuse Existing Browser Work

- Status: Superseded by [ADR 0003](0003-webview2-rendered-chrome.md)
- Date: 2026-10-07

## Context

Two existing browser projects contain useful implementation experience: the C# Brezza prototype and Microsoft's C++ WebView2Browser sample. The new application uses WPF/.NET Framework 4.8 and needs a clear boundary from both codebases so legacy state and UI assumptions do not become accidental contracts.

## Decision

Adapt the following lessons into the new architecture:

- From the C# Brezza prototype: classify URL versus search input in a pure, tested Core component; validate configurable search templates; deduplicate concurrent tab-host creation; test close/shutdown races with fake hosts.
- From WebView2Browser: treat web content as untrusted, keep browser chrome isolated from content, use a dedicated WebView2 user-data profile, and translate engine callbacks behind an adapter.
- Keep the new profile and data under `%LOCALAPPDATA%\Brezza.Next`; do not open or migrate the old `%LOCALAPPDATA%\Brezza` data automatically.

## Consequences

- Existing code is a source of tested rules and platform lessons, not a source of shared runtime state.
- The new Core has its own small contracts tailored to current workflows; legacy classes with mixed responsibilities are not copied wholesale.
- The initial native-chrome implementation was superseded before product release by ADR 0003.
- A future migration tool can be designed explicitly after the new history/bookmark formats are stable.
