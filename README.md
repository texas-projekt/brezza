# Brezza

Brezza is a lightweight Windows browser built around a search-first workflow. A thin WPF host contains separate WebView2 surfaces for trusted HTML chrome and untrusted web content. Search navigation, tabs, settings, local history, bookmarks, normal session restore, isolated private tabs, and the x64 framework-dependent package are implemented and smoke-tested.

## Build

Building requires the .NET 10 SDK. The .NET Framework 4.8 reference assemblies are restored from NuGet. Running Brezza requires Windows with .NET Framework 4.8 and the Evergreen WebView2 Runtime. User data defaults to `%LOCALAPPDATA%\Brezza.Next`; `BREZZA_DATA_DIR` can override it for isolated development runs.

```powershell
dotnet restore Brezza.slnx
dotnet build Brezza.slnx -c Release
dotnet test Brezza.slnx -c Release
```

Core and persistence tests run with `dotnet test Brezza.slnx -c Release`. The Windows chrome/content integration smoke test uses a Debug-only in-process driver and an isolated local page. The release packager verifies an extracted x64 ZIP:

```powershell
dotnet build Brezza.slnx -c Debug
powershell -ExecutionPolicy Bypass -File tools\windows-smoke\smoke.ps1
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

## Project Map

- `src/Brezza.App`: WPF host, trusted browser chrome assets, command bridge, and composition root.
- `src/Brezza.Core`: browser state and policies, independent of WPF and WebView2.
- `src/Brezza.WebView2`: WebView2 runtime adapter.
- `src/Brezza.Persistence`: local persistence implementations.
- `tests/Brezza.Core.Tests`: Core and persistence tests.
- `tools/windows-smoke`: isolated Windows WebView2 integration smoke test.
- `docs/`: product definition, architecture, decisions, roadmap, and changelog.

See [Product Definition](docs/PRODUCT.md), [Architecture](docs/ARCHITECTURE.md), and [Roadmap](docs/ROADMAP.md) before changing product or system boundaries.
