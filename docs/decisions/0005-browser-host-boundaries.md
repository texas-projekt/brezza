# ADR 0005: Browser host boundaries and lifecycle outcomes

- Status: Accepted
- Date: 2026-10-12

## Context

The browser needs to enforce the trusted-chrome boundary throughout navigation, report whether private data was actually removed, and avoid releasing tabs that are still loading or playing media. Downloads, popup destinations, and permission requests also need explicit browser behavior.

## Decision

- Content hosts start with WebView2 messaging disabled. Browser-initiated navigation to the exact trusted origin enables messaging; remote documents and remote frames cannot navigate into that origin. Native message handling checks the source, and Core restricts commands by internal page. Local assets use `DenyCors` mapping.
- Private-session cleanup returns `Completed`, `Pending`, or `Failed`, including its path, attempt count, and failure detail. Completion requires verified removal. Unremoved paths stay tracked. Cleanup attempts are serialized, and timer retries wait until no private tabs are open so a new private session is not deleted. Shutdown performs a final attempt.
- Core evaluates tab loading, audio, and full-screen signals before releasing an inactive host. The existing eight-host budget and ten-minute inactivity window remain. Sleeping releases the host; reactivation reloads the saved URL and does not promise DOM or form restoration.
- Core sanitizes suggested download names and selects collision suffixes using an existence check supplied by the adapter. Core performs no filesystem I/O. The adapter writes to the configured downloads folder and reports start, completion, and failure; there is no progress or cancellation UI yet.
- Popup requests retain the originating tab's session kind. HTTP/HTTPS destinations open as tabs; a blank request opens an empty tab. Other popup schemes are ignored. This basic path does not preserve the complete `window.opener` scripting relationship.
- Permission requests are currently denied with a localized notice. Allowing microphone, camera, location, and other capabilities needs a future prompt implementation.

## Consequences and validation

`ITabHost` exposes lifecycle signals and popup/download/permission events. `ITabHostFactory.CleanupSessionAsync` returns a cleanup result. Core owns eligibility, session classification, and per-page command rules; WebView2 owns runtime translation and cleanup I/O; App renders notices and issues commands.

No packages or rendering frameworks are added. Core tests cover recent-tab retention, working-tab eligibility, cleanup retry concurrency and live-session protection, popup session classification, download names, and command allowlists. Windows smoke checks internal-page state delivery, the remote-content boundary, private-profile cleanup, and session restore. Popup user gestures, downloads through the real Runtime, and permission prompts have not received full end-to-end coverage.
