using System;
using System.Collections.Generic;

namespace Brezza.App.Native;

/// <summary>
/// Standard dialog keyboard behavior for the shell's own windows: Tab and Shift+Tab move between
/// controls, Enter sends <c>IDOK</c>, and Escape sends <c>IDCANCEL</c>. The single message loop asks
/// this class before dispatching, because a window registered with its own class does not get
/// <c>IsDialogMessage</c> handling the way a dialog-template window does.
/// </summary>
internal static class DialogKeyboard
{
    private static readonly List<IntPtr> Dialogs = new();

    public static void Register(IntPtr dialog)
    {
        if (dialog != IntPtr.Zero && !Dialogs.Contains(dialog)) Dialogs.Add(dialog);
    }

    public static void Unregister(IntPtr dialog) => Dialogs.Remove(dialog);

    /// <summary>Lets a registered window handle a keyboard message; returns true when it was consumed.</summary>
    public static bool TryTranslate(ref NativeMethods.MSG message)
    {
        foreach (var dialog in Dialogs)
        {
            if (message.hwnd != dialog && !NativeMethods.IsChild(dialog, message.hwnd)) continue;
            return NativeMethods.IsDialogMessage(dialog, ref message);
        }

        return false;
    }
}
