using System;

namespace Brezza.Core.Abstractions;

public sealed class BrowserShortcutEventArgs : EventArgs
{
    public BrowserShortcutEventArgs(int virtualKey, bool control, bool alt, bool shift)
    {
        VirtualKey = virtualKey;
        Control = control;
        Alt = alt;
        Shift = shift;
    }

    public int VirtualKey { get; }

    public bool Control { get; }

    public bool Alt { get; }

    public bool Shift { get; }

    public bool Handled { get; set; }
}
