# Brezza

Brezza is a lightweight Windows browser built around a search-first workflow. Its shell is native Win32: the window, tab strip, address/search field, navigation buttons, notices, and settings/history/bookmark panels are real HWND controls, and WebView2 renders only website content in a separate surface. Search navigation, tabs, settings, local history, bookmarks, opt-in session restore, isolated private tabs, native System/Light/Dark themes, and the x64 framework-dependent package are implemented and smoke-tested. Basic downloads report their results, popup destinations open as tabs, and permission requests are denied with a notice until approval UI is added.

## Build and test

Building requires the .NET 10 SDK. The .NET Framework 4.8 reference assemblies are restored from NuGet. Running Brezza requires Windows with .NET Framework 4.8 and the Evergreen WebView2 Runtime. User data defaults to `%LOCALAPPDATA%\Brezza`; `BREZZA_DATA_DIR` can override it for isolated development runs.

```powershell
dotnet restore Brezza.slnx
dotnet build Brezza.slnx -c Release
```

`tools/test.ps1` is the single entry point for the test suite. `-Stage` picks one layer — `Unit`, `Smoke`, `Package`, or `Visual` — and the default `All` runs them in order, stopping at the first failure:

```powershell
powershell -ExecutionPolicy Bypass -File tools\test.ps1
```

- `Unit` runs the Core and persistence tests (`dotnet test tests/Brezza.Core.Tests`) and writes TRX results to `artifacts/test-results/`.
- `Smoke` builds Debug and runs the Windows integration smoke, which uses a Debug-only in-process driver that exercises the real native controls against an isolated local page. Its inspection switches are parameters (`-LongNotice`, `-PauseAfterFailureMs`, `-PauseAfterSettingsMs`, `-PauseAfterHistoryMs`, `-PauseAfterBookmarksMs`, `-Keep`).
- `Package` runs the x64 framework-dependent packager, which verifies an extracted ZIP; `-PackageVersion` names the archive and `-SkipLaunchVerification` skips the launch check.
- `Visual` builds Debug and captures the whole native UI baseline in one pass. It needs an interactive desktop, takes a few minutes, and produces screenshots for review rather than a pass or fail; `-VisualOnly <Scenario>` captures a single one (see [UI Visual Baseline](docs/UI_VISUAL_BASELINE.md)).

CI calls the same entry point (`tools/test.ps1 -Stage Unit`) for the stage its runners can run. The test layers, conventions, and when to run each are described in [Testing](docs/TESTING.md).

`BREZZA_TRACE_LOG` optionally writes diagnostic trace output to a file, which is useful when investigating native lifetime problems.

For a visual check of the native shell, start the Debug executable and capture its top-level window with `tools/capture-window.ps1`; the script writes a Windows BMP without requiring a UI framework or browser-page bridge. Use `-Width 640 -Height 420` for the minimum-window check, and repeat at the machine's 125%/150% display scale for DPI comparison.

## Project Map

- `src/Brezza.App`: native Win32 shell (window, controls, message loop, localization, native panels) and composition root.
- `src/Brezza.Core`: browser state and policies, independent of Win32 and WebView2.
- `src/Brezza.WebView2`: WebView2 runtime adapter that hosts controllers in HWNDs.
- `src/Brezza.Persistence`: local persistence implementations.
- `tests/Brezza.Core.Tests`: Core and persistence tests.
- `tools/test.ps1`: the single entry point for the unit tests, the Windows smoke, and the package check.
- `tools/smoke.ps1`: isolated Windows native-control integration smoke test.
- `tools/capture-window.ps1`: scenario-driven window capture for the visual baseline (`-Scenario`).
- `tools/package.ps1`: x64 framework-dependent packaging and extracted-package verification.
- `docs/`: product definition, architecture, decisions, roadmap, changelog, and the testing guide.

See [Product Definition](docs/PRODUCT.md), [Architecture](docs/ARCHITECTURE.md), and [Roadmap](docs/ROADMAP.md) before changing product or system boundaries.

The screenshot scenarios and review criteria are listed in [UI Visual Baseline](docs/UI_VISUAL_BASELINE.md).
