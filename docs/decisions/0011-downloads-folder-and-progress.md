# ADR 0011: A configurable downloads folder and download progress controls

- Status: Accepted
- Date: 2026-10-17

## Context

Downloads were a notification, not a managed thing: the browser wrote files to
`%USERPROFILE%\Downloads`, reported the result as a notice, and offered no list, no control, and no
choice of destination. `PRODUCT.md` recorded "Download progress controls … are not implemented" and
the downloads folder as fixed, and ADR 0005 covered only naming and the runtime's own dialog being
suppressed.

Two things were missing for downloads to be managed the way cookies and browsing data already are:

- The destination was not the user's to choose. A browser that owns the downloads folder cannot be
  pointed at another drive, a shared folder, or a per-project folder, and the only way to change it
  was to move the files afterwards.
- A running download was invisible until it finished. The runtime reports `BytesReceivedChanged`
  often and exposes `Pause`, `Resume`, and `Cancel`, so progress and control cost nothing the
  browser was not already given — but nothing surfaced them.

## Decision

Downloads are managed from the downloads panel, and the destination is a setting.

**The destination is a setting, not a constant.** `BrowserSettings` gains a `downloadsFolder`
(schema 4) that normalizes to a full path or falls back to empty, which means the user's own
Downloads folder. The settings panel grows a row with a path field and a Browse button that opens the
system folder picker (`IFileDialog` with `FOS_PICKFOLDERS`), so a folder is picked rather than
typed. The factory resolves the folder per download rather than capturing it, so a new folder applies
to the next download without recreating the WebView2 environments or restarting the browser.

**A running download is visible and controllable.** The row shows what has arrived against the
total, the share when the runtime knows one, and a thin bar along its bottom. Its own buttons pause
and resume, and cancel behind a confirmation; a finished row keeps the folder and remove buttons it
already had.

The runtime reports a paused download as *interrupted*, with the interrupt reason `UserPaused`. The
adapter maps that one reason to `BrowserDownloadState.Paused`, so a pause is not a failure and the
download keeps its operation and its progress events — it can be resumed and reports where it
stopped. `Cancel` ends the download through the runtime's own interruption, with the reason
`UserCanceled`.

`BrowserDownloadState` gains `Paused` after the existing values, so a stored record's state number
never changes meaning. A paused download is not terminal, so it is never written to `downloads.xml`
and a browser that closes loses it, exactly as an in-progress one does.

## Consequences

- `ITabHost` grows `DownloadProgressChanged` and `TryPauseDownload`, `TryResumeDownload`,
  `TryCancelDownload`. A download belongs to the tab that started it, so `TabRuntimeManager` routes
  the command to the host that owns it — the same shape as `GetFaviconAsync(string tabId)` — and the
  registry records only what the adapter reports.
- Progress is reported often and shown rarely. The registry keeps every value; the shell refreshes
  the panel on a fixed cadence and never saves the store for progress, because only a finished
  record belongs on the device.
- The settings schema moves to 4 and `SettingsStore` accepts schema 3 on load, so an existing
  profile keeps its search provider, language, theme, and startup choice and simply has no saved
  folder.
- The system folder picker is the one new COM surface. It is created through its coclass, shown
  owned by the panel, and a dialog the system refuses is reported as a dismissal rather than thrown.
- The Windows smoke drives the whole path against a slow attachment served by the local origin: the
  download is paused from its row, keeps its bytes, resumes, completes, and a second one is cancelled
  behind its confirmation. The visual baseline cannot show a running download, because a seeded
  `downloads.xml` only ever holds finished records — the same reason the cookies panel is not a
  scenario.
