# Testing

The suite is layered. Each layer has one tool, one command, and a defined place in CI, and
`tools/test.ps1` runs them from a single entry point.

| Layer | Tool | Command | In CI |
|---|---|---|---|
| Unit (Core, Persistence) | xUnit | `tools/test.ps1 -Stage Unit` (`dotnet test tests/Brezza.Core.Tests`, TRX in `artifacts/test-results/`) | Yes |
| Native-shell integration | `tools/smoke.ps1` | `tools/test.ps1 -Stage Smoke` (builds Debug first) | No — needs an interactive desktop and the WebView2 Runtime |
| Visual baseline | `tools/capture-baseline.ps1` | `tools/test.ps1 -Stage Visual` (builds Debug, drives every automatable scenario) | No — needs an interactive desktop |
| Package verification | `tools/package.ps1` | `tools/test.ps1 -Stage Package` | Release workflow only, with `-SkipLaunchVerification` |

## Conventions

- Test files are named `<Type>Tests.cs` and mirror the source tree under `tests/Brezza.Core.Tests`.
- Tool scripts live flat in `tools/`; `test.ps1` is the only entry point and the others are named
  for their purpose.
- Every screenshot is produced by `tools/capture-window.ps1 -Scenario <Name>`, which owns the
  canonical window geometry per scenario and records the capture state in
  `artifacts/capture-manifest.json`; the captured region is never set by hand.
  `capture-baseline.ps1` drives the shell through the scenarios that can be reached from outside
  the application.
- Test results land in `artifacts/test-results/` and screenshots in `artifacts/`; both are ignored
  by Git.
- The smoke driver is compiled into `Brezza.App` under `#if DEBUG` and split into
  `BrowserShell.Smoke*.cs` partial files by concern (navigation, native input, panels, data
  panels). Driving the real HWNDs from inside the process is deliberate: it exercises the shipped
  controls rather than a parallel test surface, and the driver needs the shell's internals, which
  a separate project would not have.
- The smoke page's favicon is a test-tube emoji in an inline SVG data URI: a meaningful icon with
  no asset or license, which the favicon assertions then verify end to end.

## When to run what

- Every change: `tools/test.ps1 -Stage Unit`.
- Changes to the native shell, panels, tabs, notices, or lifecycle: add `-Stage Smoke`.
- Before a release, or after a change to the shell's chrome: the package stage and the visual
  baseline capture (`pwsh tools/test.ps1 -Stage Visual`, reviewed against
  [UI Visual Baseline](UI_VISUAL_BASELINE.md)).
- One visual scenario after a change to that surface:
  `pwsh tools/test.ps1 -Stage Visual -VisualOnly <Scenario>`.

## Industry reference

- WebView2 content is commonly driven with Playwright over CDP: the app is launched with
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port` and an isolated
  `WEBVIEW2_USER_DATA_FOLDER`, then attached with `ConnectOverCDPAsync`. It covers page content
  only, not the native chrome. Adopting it would need a decision record (a new dependency).
- Native Win32 chrome is commonly driven with Windows UI Automation (FlaUI, WinAppDriver).
- Whether the smoke can run on GitHub's `windows-latest` runner is unverified; the workflows
  currently assume it cannot. A `workflow_dispatch` spike would settle it.
