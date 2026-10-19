# ADR 0009: Single-executable packaging by assembly merge

- Status: Accepted
- Date: 2026-10-14

## Context

The release package ships four managed assemblies (`Brezza.exe`, `Brezza.Core.dll`,
`Brezza.Persistence.dll`, `Brezza.WebView2.dll`) plus `Microsoft.Web.WebView2.Core.dll`, the native
`WebView2Loader.dll`, and the localized language packs. Users asked for a portable build with as few
files as possible.

.NET Framework has no single-file publish: `PublishSingleFile` exists only for .NET Core 3.0 and
later. Two of the shipped files cannot be merged into an assembly at all, because Windows loads a
native DLL from disk and the language packs are content files read with `File.ReadAllText`.

## Decision

Merge the managed assemblies into `Brezza.exe` with ILRepack, run from a repository-pinned local
tool (`.config/dotnet-tools.json`) during packaging only. The package then holds:

- `Brezza.exe` (all managed assemblies)
- `WebView2Loader.dll` (native, cannot merge)
- `BrowserUi/locales/{en,zh-Hans,zh-Hant}.json` (content, not merged)

ILRepack runs without `/internalize`: internalizing the WebView2 interop types breaks
`CoreWebView2.Raw.EventRegistrationToken` and the content surface never appears. The merged output
is written under a name that keeps the assembly identity `Brezza`; merging to any other file name
leaves the identity wrong and WebView2 fails to load it.

Normal builds stay unmerged, so Debug runs and the Windows smoke keep the original assemblies and
stay debuggable. Only `tools/package.ps1` merges, and its launch verification then proves the merged
executable starts with native controls and a WebView2 surface. `-SkipLaunchVerification` skips that
step for CI runs that have no interactive desktop.

## Consequences

The package is one executable plus the native loader and the language packs. Adding an assembly to
the solution means adding it to the merge list in `tools/package.ps1`. ILRepack is a packaging-time
dependency only, pinned in the tool manifest (`dotnet-ilrepack` 2.0.48), and its version is recorded here rather than
introduced silently. A true single file would require embedding the language packs as resources and
extracting the native loader at runtime, or porting to .NET; both are out of scope.
