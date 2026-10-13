# Brezza Project Rules

- Keep product scope in `docs/PRODUCT.md`, architecture in `docs/ARCHITECTURE.md`, decisions in `docs/decisions/`, and delivery status in `docs/ROADMAP.md`.
- Update the relevant document when a code change alters an interface, ownership rule, product commitment, or milestone acceptance criterion.
- Preserve the dependency direction: App may depend on Core, WebView2, and Persistence; WebView2 and Persistence may depend on Core; Core has no project dependencies.
- The browser UI is native Win32 (ADR 0007): the window, tab strip, address/search field, navigation buttons, notices, and settings/history/bookmark panels are real HWND controls owned by `Brezza.App` through P/Invoke. WebView2 renders website content only. Do not reintroduce WPF, WinForms control abstractions, WinUI, Avalonia, or Electron for browser UI, and do not render browser chrome as HTML in a WebView2 surface.
- Core owns browser state and policy. UI code renders state and sends commands; it must not own a second authoritative copy.
- Do not expose WebView2 objects or general-purpose script execution to remote page content. Content hosts keep the message bridge disabled and register no message handler.
- Keep async lifecycle operations observable. Do not discard tasks that can fail without an explicit error-handling path.
- Do not add frameworks or packages without recording the need in a decision record.
- Before finishing a change, run the narrowest relevant build or test and report any check that could not run.
