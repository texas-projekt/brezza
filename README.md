# Brezza

Brezza is a lightweight, search-first browser for Windows. One field takes a search or an address,
and the rest of the window stays out of the way. The window and all browser controls are native
Windows controls; web pages are rendered by Microsoft Edge WebView2.

## Install

Requires Windows 10 or 11 (x64) with .NET Framework 4.8 and the
[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). Brezza tells you on
start if the runtime is missing.

1. Download `Brezza-<version>-win-x64-native.zip` and its `.sha256` file from
   [Releases](https://github.com/texas-projekt/brezza/releases).
2. Releases are not code-signed, so check the archive first: the output of
   `(Get-FileHash <zip> -Algorithm SHA256).Hash` must match the `.sha256` file.
3. Extract it anywhere and run `Brezza.exe`. Nothing is installed.

## Using Brezza

- **Search or go:** type into the address field and press Enter. Nothing reaches the search provider
  until you do. A new tab puts the cursor there.
- **New-tab page:** lists recent pages and bookmarks.
- **Tabs:** close a tab from the × it shows on hover, with a middle-click, or with Ctrl+W.
  Right-click a tab to reload, duplicate, or close it, the others, or the tabs to its right. A
  duplicate or a popup opens next to the tab it came from. Closing the last tab, private or not,
  leaves one new normal tab. A tab playing sound shows a speaker.
- **Loading:** while a page loads, the reload button stops it. When a page cannot load, the notice
  says why and offers Try again when that can help.
- **Private tabs:** not saved to history or the session; their cookies and site data are deleted
  when the last private tab closes. Downloaded files still go to your downloads folder.
- **Panels:** history, downloads, bookmarks, cookies, browsing data, and settings open from the ···
  menu. Downloads can be paused, resumed, and cancelled; up to three run at once, and a file known to
  exceed 8 GiB is refused.
- **Settings:** search provider, language (English, Simplified or Traditional Chinese), theme, what
  opens on start, and the downloads folder.

| Shortcut | Action |
|---|---|
| Ctrl+T / Ctrl+Shift+N | New tab / new private tab |
| Ctrl+W | Close tab |
| Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+1…9 | Switch tabs |
| Ctrl+L, Ctrl+K | Address field |
| F5 / Esc | Reload / stop |
| Alt+Left / Alt+Right | Back / forward |
| Ctrl+D | Bookmark the page |
| Ctrl+H, Ctrl+J, Ctrl+Shift+Delete, Alt+F | History, downloads, browsing data, menu |

## Privacy

- Your data stays in `%LOCALAPPDATA%\Brezza`. No accounts, sync, or telemetry.
- Pages cannot talk to the browser and cannot open other applications without your click.
- Permission requests (camera, location, …) are denied with a notice; there is no prompt yet.
- Clearing history, cookies, browsing data, or the download list asks first. Clearing the download
  list never deletes the files.

Not available yet: permission prompts, find in page, zoom, printing, a bookmarks bar, drag and drop.
Report bugs as [issues](https://github.com/texas-projekt/brezza/issues); report security problems
privately to the maintainer instead.

## Development

Needs Windows and the .NET 10 SDK pinned in `global.json`. How the code is organized, its rules, and
the decisions behind them are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

```powershell
dotnet build Brezza.slnx -c Release
dotnet test tests/Brezza.Core.Tests -c Release     # every change; also runs in CI
pwsh tools/smoke.ps1                               # after changing the shell, tabs, panels, or navigation
pwsh tools/package.ps1 -Version 1.0.0              # before a release
```

The unit tests cover Core, Persistence, and the language packs. The smoke drives the real window
through the main paths in about 20 seconds. The smoke and the package launch check need an
interactive desktop and no other Brezza window open. `BREZZA_DATA_DIR` points the browser at another
data folder.

Commits follow Conventional Commits. A `v<semver>` tag builds a GitHub Release; write its notes by
hand from the commits since the last tag.

## License

[Apache-2.0](LICENSE). The release also includes the Microsoft WebView2 SDK license in
`THIRD-PARTY-NOTICES.txt`.
