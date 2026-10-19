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
    private static readonly List<(IntPtr Handle, bool TabOnly)> Dialogs = new();

    /// <param name="dialog">The window whose child controls take part in dialog navigation.</param>
    /// <param name="tabOnly">
    /// When true, only Tab and Shift+Tab are translated. The main browser window uses this so Enter
    /// in the address field still reaches the address edit's own subclass instead of becoming IDOK.
    /// </param>
    public static void Register(IntPtr dialog, bool tabOnly = false)
    {
        if (dialog == IntPtr.Zero || Dialogs.Exists(entry => entry.Handle == dialog)) return;
        Dialogs.Add((dialog, tabOnly));
    }

    public static void Unregister(IntPtr dialog) => Dialogs.RemoveAll(entry => entry.Handle == dialog);

    /// <summary>Lets a registered window handle a keyboard message; returns true when it was consumed.</summary>
    public static bool TryTranslate(ref NativeMethods.MSG message)
    {
        var tabOnlyKey = message.message == NativeMethods.WM_KEYDOWN &&
            NativeMethods.GetLoWord(message.wParam) == NativeMethods.VK_TAB;

        foreach (var entry in Dialogs)
        {
            if (message.hwnd != entry.Handle && !NativeMethods.IsChild(entry.Handle, message.hwnd)) continue;
            if (entry.TabOnly && !tabOnlyKey) continue;
            return NativeMethods.IsDialogMessage(entry.Handle, ref message);
        }

        return false;
    }
}
