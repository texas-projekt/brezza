# Native UI visual baseline

The native shell is checked with `tools/capture-window.ps1` and the Debug Windows smoke harness.
The screenshots are generated under `artifacts/` and are intentionally ignored by Git.

| Scenario | Evidence | What to inspect |
|---|---|---|
| Dark new-tab page | `artifacts/brezza-address-cuebanner.bmp` | Startup title, subtitle, address cue banner, dark content background |
| Light new-tab page | `artifacts/brezza-light-theme.bmp` | Light palette, readable text, address cue banner |
| Minimum window | `artifacts/brezza-min-window-icons.bmp` | Compact toolbar, address width, no button overlap |
| 150% DPI | `artifacts/brezza-dpi-150.bmp` | Scaled toolbar and tab controls |
| Settings panel | `artifacts/brezza-settings-panel-fixed.bmp` | Localized labels, buttons, theme row |
| Dark settings controls | `artifacts/brezza-settings-combos-reverted.bmp` | Combo-box text and dark surface contrast |
| History panel | `artifacts/brezza-history-panel-fixed.bmp` | Localized button widths and list row layout |
| Empty bookmarks | `artifacts/brezza-bookmarks-panel-empty.bmp` | Empty-state message and disabled actions |
| Failure notice | `artifacts/brezza-notice-zorder.bmp` | Notice text visibility, dark notice palette, reload/dismiss actions |
| Long notice text | `artifacts/brezza-notice-ellipsis.bmp` | Long detail text is ellipsized before the reload/dismiss actions |
| Active-tab close affordance | `artifacts/brezza-close-tab-button.bmp` | Discoverable close button without tab-row overlap |
| Tab-strip wheel switching | `artifacts/brezza-tabs-restored.bmp` | Wheel direction changes the Core active tab without disturbing the row |
| Chinese native toolbar | `artifacts/brezza-smoke-zh-startup2.bmp` | Localized toolbar labels and private-tab action remain readable |
| Loading tab title | `artifacts/brezza-loading-state.bmp` | Active tab shows a loading ellipsis without changing toolbar geometry |
| Settings keyboard focus | `artifacts/brezza-settings-focus-save.bmp` | Native panel focus target remains visible and the button layout stays stable |

The DWM title-bar color attributes are applied when available, but this Windows environment keeps the
system title bar light even while the client area follows the dark palette. Treat that as an environment
limitation during screenshot review; do not infer a content-area theme regression from it.

For a repeatable minimum-size capture:

```powershell
powershell -ExecutionPolicy Bypass -File tools\windows-smoke\smoke.ps1
$process = Get-Process Brezza | Select-Object -First 1
powershell -ExecutionPolicy Bypass -File tools\capture-window.ps1 `
  -ProcessId $process.Id -Width 640 -Height 420 -OutputPath artifacts\current-min-window.bmp
```

Use `-FocusControlId 1018` (the toolbar menu button) or another native control ID to capture keyboard-focus states.

For automated long-notice verification, set `BREZZA_SMOKE_LONG_NOTICE=1` before running the Debug
Windows smoke script. The harness injects a long failure detail after the normal navigation failure
assertion and keeps the existing reload/dismiss geometry under test.

