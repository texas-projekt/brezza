# Brezza

Brezza is a lightweight Windows browser built around a search-first workflow. Its shell is native Win32: the window, tab strip, address/search field, navigation buttons, notices, and settings/history/bookmark panels are real HWND controls, and WebView2 renders only website content in a separate surface. Search navigation, tabs, settings, local history, bookmarks, normal session restore, isolated private tabs, native System/Light/Dark themes, and the x64 framework-dependent package are implemented and smoke-tested. Basic downloads report their results, popup destinations open as tabs, and permission requests are denied with a notice until approval UI is added.

## Build

Building requires the .NET 10 SDK. The .NET Framework 4.8 reference assemblies are restored from NuGet. Running Brezza requires Windows with .NET Framework 4.8 and the Evergreen WebView2 Runtime. User data defaults to `%LOCALAPPDATA%\Brezza`; `BREZZA_DATA_DIR` can override it for isolated development runs.

```powershell
dotnet restore Brezza.slnx
dotnet build Brezza.slnx -c Release
dotnet test Brezza.slnx -c Release
```

Core and persistence tests run with `dotnet test Brezza.slnx -c Release`. The Windows integration smoke test uses a Debug-only in-process driver that exercises the real native controls and an isolated local page. The release packager verifies an extracted x64 ZIP:

```powershell
dotnet build Brezza.slnx -c Debug
powershell -ExecutionPolicy Bypass -File tools\windows-smoke\smoke.ps1
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

`BREZZA_TRACE_LOG` optionally writes diagnostic trace output to a file, which is useful when investigating native lifetime problems.

For a visual check of the native shell, start the Debug executable and capture its top-level window with `tools/capture-window.ps1`; the script writes a Windows BMP without requiring a UI framework or browser-page bridge. Use `-Width 640 -Height 420` for the minimum-window check, and repeat at the machine's 125%/150% display scale for DPI comparison.

## Project Map

- `src/Brezza.App`: native Win32 shell (window, controls, message loop, localization, native panels) and composition root.
- `src/Brezza.Core`: browser state and policies, independent of Win32 and WebView2.
- `src/Brezza.WebView2`: WebView2 runtime adapter that hosts controllers in HWNDs.
- `src/Brezza.Persistence`: local persistence implementations.
- `tests/Brezza.Core.Tests`: Core and persistence tests.
- `tools/windows-smoke`: isolated Windows native-control integration smoke test.
- `tools/package.ps1`: x64 framework-dependent packaging and extracted-package verification.
- `docs/`: product definition, architecture, decisions, roadmap, and changelog.

See [Product Definition](docs/PRODUCT.md), [Architecture](docs/ARCHITECTURE.md), and [Roadmap](docs/ROADMAP.md) before changing product or system boundaries.

The screenshot scenarios and review criteria are listed in [UI Visual Baseline](docs/UI_VISUAL_BASELINE.md).
