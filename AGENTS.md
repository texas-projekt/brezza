# Brezza Project Rules

- There are two project documents. A user-visible change updates `README.md`. A change to an interface, an ownership rule, an invariant, or a decision updates `docs/ARCHITECTURE.md`, and a new decision gets an entry there. Do not add other docs.
- Dependency direction: App → Core, WebView2, Persistence; WebView2 → Core; Persistence → Core; Core has no project references.
- Browser chrome is native Win32 HWND controls owned by `Brezza.App` through P/Invoke. WebView2 renders website content only. Do not add WPF, WinForms controls, WinUI, Avalonia, or Electron, and do not render chrome as HTML.
- Core owns browser state and policy. UI code renders state and sends commands; it never keeps a second authoritative copy.
- Content hosts keep the message bridge disabled and register no message handler. Never expose WebView2 objects or general script execution to page content.
- Do not discard async tasks that can fail; every one needs an error path (`Observe` in the shell).
- No new frameworks or packages without a decision entry in `docs/ARCHITECTURE.md`.
- Before finishing, run the narrowest relevant check (`dotnet test tests/Brezza.Core.Tests`, plus `pwsh tools/smoke.ps1` for shell changes) and report anything that could not run. Put behavior details in unit tests; keep the smoke to the main paths.
