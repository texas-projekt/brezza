# Native UI visual baseline

One tool, one invocation form. A single screenshot in this baseline is produced by

```powershell
pwsh tools/capture-window.ps1 -Scenario <Name>
```

which resolves the window, normalizes its geometry from the scenario preset, waits for the
window's painting to settle, and writes `artifacts/<scenario>.png`. Nothing about the captured
region is set by hand: a capture is always the whole window at the scenario's canonical size, so
the same scenario produces comparable images on any machine. `-Scenario` with an unknown name
lists every valid one.

Every capture records its state in `artifacts/capture-manifest.json`: scenario, window class,
rectangle, DPI, pinned or window-owned geometry, focused control, theme, language, capture time,
and build. Re-capturing a scenario replaces its file and its manifest entry.

## What the capture guarantees

- **Modern frame.** The window is rendered by the system compositor (`PrintWindow` with
  `PW_RENDERFULLCONTENT`), so a screenshot shows the window's real frame — never the legacy
  frame a plain print produces.
- **No desktop.** Pixels are composed into a private bitmap that never reads the screen, and the
  window must be visible and not minimized. An area the window does not paint stays magenta:
  magenta in a screenshot means the render is incomplete, not a UI defect.
- **Deterministic timing.** The tool waits until the window has no pending paint instead of
  sleeping a fixed time; the manifest records whether it settled.
- **Screen orientation.** The capture shows the window as it appears on screen, top row first.
  The two halves of the pixel path disagree about row order — `GetDIBits` hands back a bottom-up
  buffer while the bitmap the PNG is built from is top-down — so the rows are reversed on the way
  across. A capture with the window's caption bar at the bottom is the symptom of that step being
  skipped.
- **One geometry per scenario.** The scenario preset owns the size. Main-window scenarios are
  pinned to 1120x760 logical pixels (the shell's own default), scaled to the window's DPI; the
  minimum-window scenario pins 640x420. Panels and the toolbar menu keep the size their content
  gives them, which is deterministic for the same content and DPI.

## Scenarios

| Scenario | Evidence | What to inspect |
|---|---|---|
| NewTabDark | `artifacts/brezza-newtab-dark.png` | Startup title, subtitle, address cue banner, dark content background |
| NewTabLight | `artifacts/brezza-newtab-light.png` | Light palette, readable text, address cue banner |
| MinimumWindow | `artifacts/brezza-min-window.png` | Compact toolbar, address width, no button overlap |
| Dpi150 | `artifacts/brezza-dpi-150.png` | Scaled toolbar and tab controls |
| SettingsPanel | `artifacts/brezza-settings-panel.png` | Localized labels, buttons, theme row, startup row |
| SettingsDarkControls | `artifacts/brezza-settings-combos.png` | Combo-box text and dark surface contrast |
| SettingsFocus | `artifacts/brezza-settings-focus.png` | Keyboard focus target remains visible and the button layout stays stable |
| HistoryPanel | `artifacts/brezza-history-panel.png` | Localized button widths and list row layout |
| BookmarksEmpty | `artifacts/brezza-bookmarks-empty.png` | Empty-state message and disabled actions |
| DataPanel | `artifacts/brezza-data-panel.png` | The four data categories with what each covers, a clear action per row, and the clear-all footer; no size summary |
| ToolbarMenu | `artifacts/brezza-toolbar-menu.png` | All five browser-owned views are menu entries: history, bookmarks, cookies, browsing data, and settings |
| FailureNotice | `artifacts/brezza-notice.png` | Notice text visibility, dark notice palette, reload/dismiss actions |
| LongNotice | `artifacts/brezza-notice-ellipsis.png` | Long detail text is ellipsized before the reload/dismiss actions |
| CloseAffordance | `artifacts/brezza-close-tab-button.png` | Discoverable close button without tab-row overlap |
| TabsCrowded | `artifacts/brezza-tabs-floor.png` | Tabs keep the minimum width and the strip scrolls with no visible scroll control; the active tab's close button stays on screen |
| TabsIndicatorLeft | `artifacts/brezza-tabs-indicator-left.png` | The indicator's thumb sits at the start of the track |
| TabsIndicatorMiddle | `artifacts/brezza-tabs-indicator-middle.png` | A thin indicator under the tabs: the thumb's width is the share of the strip that is visible and its place is where the view sits; the new-tab and caption buttons stay visible while the strip scrolls |
| TabsIndicatorRight | `artifacts/brezza-tabs-indicator-right.png` | The indicator's thumb sits at the end of the track |
| TabFavicon | `artifacts/brezza-tabs-favicon.png` | A tab's own favicon leads its label, which ellipsizes as the tab narrows; the close button stays |
| TabGlobe | `artifacts/brezza-tabs-globe.png` | A page with no favicon leads with the built-in globe glyph from the shell's icon font |
| TabPrivate | `artifacts/brezza-tabs-private.png` | A private tab leads with its private glyph alone and keeps the page's own title |
| TabsWheel | `artifacts/brezza-tabs-restored.png` | Wheel direction changes the Core active tab without disturbing the row |
| ToolbarZh | `artifacts/brezza-toolbar-zh.png` | Localized toolbar labels and private-tab action remain readable |
| TabLoading | `artifacts/brezza-loading-state.png` | Active tab shows a loading ellipsis without changing toolbar geometry |

## Producing the baseline

One command produces every scenario that can be reached from outside the application:

```powershell
pwsh tools/test.ps1 -Stage Visual
```

It builds Debug, then `tools/capture-baseline.ps1` drives the shell through the scenarios —
launching it with a seeded data root (theme, language, history, session) and sending the window
messages a user's input produces: commands by control id and a wheel notch over the tab strip.
Pages a scenario needs are opened by the shell's own session restore, so no address is typed.
No application code is involved, so the pass is reproducible; it takes a few minutes and needs
the interactive desktop.

To re-capture a single scenario after a change to that surface:

```powershell
pwsh tools/capture-window.ps1 -Scenario <Name>
```

Two scenarios are not automatable and keep a manual recipe:

- **Dpi150** — the scenario is the shell at 150% display scaling. Set the display to 150% first,
  then `pwsh tools/capture-window.ps1 -Scenario Dpi150`; the preset stays logical (1120x760) and
  the tool scales it to the window's DPI.
- **LongNotice** — its long failure detail is injected by the smoke driver inside the
  application, which an external driver cannot reach. Run
  `pwsh tools/smoke.ps1 -LongNotice -PauseAfterFailureMs 20000` in one terminal and capture with
  `pwsh tools/capture-window.ps1 -Scenario LongNotice` in another while the notice is held.

The cookies panel is deliberately not a scenario: its rows are the machine's own cookies, so a
screenshot of it is not a reproducible baseline.

## Environment notes

- The capture needs an interactive desktop: the shell is a real Win32 window and content pages
  load the WebView2 Runtime. It cannot run on a headless CI runner.
- The DWM title-bar color attributes are applied when available, but this Windows environment
  keeps the system title bar light even while the client area follows the dark palette. Treat
  that as an environment limitation; do not infer a content-area theme regression from it.
- The `.png` names above supersede the earlier `.bmp` evidence produced by the previous capture
  script; a capture whose manifest entry is missing was not produced by the current tool.
