#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Brezza.App.Native;
using Brezza.Core.Tabs;

// Debug-only smoke helpers for native input: toolbar buttons, the tab strip, and the
// assertions that belong to them.
namespace Brezza.App;

internal sealed partial class BrowserShell
{

    private void ClickButton(int controlId)
    {
        var control = _window.GetControl(controlId);
        if (control == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native control " + controlId + " does not exist.");
        }

        NativeMethods.SendMessage(
            _window.Handle,
            NativeMethods.WM_COMMAND,
            new IntPtr(NativeMethods.MakeLong(controlId, NativeMethods.BN_CLICKED)),
            control);
    }

    /// <summary>
    /// Selects a tab the way a user does. A tab control does not notify for TCM_SETCURSEL, so the
    /// smoke drives the real input path: a left click on the tab strip item.
    /// </summary>
    private void SelectTab(int index)
    {
        var strip = _window.GetControl(MainWindow.IdTabs);
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(strip, NativeMethods.TCM_GETITEMRECT, new IntPtr(index), ref rect);
        var position = new IntPtr(NativeMethods.MakeLong(rect.left + 6, rect.top + Math.Max(1, rect.Height / 2)));
        NativeMethods.SendMessage(strip, NativeMethods.WM_LBUTTONDOWN, new IntPtr(0x0001), position);
        NativeMethods.SendMessage(strip, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, position);
    }

    private void MiddleClickTab(int index)
    {
        var strip = _window.GetControl(MainWindow.IdTabs);
        var rect = new NativeMethods.RECT();
        NativeMethods.SendMessage(strip, NativeMethods.TCM_GETITEMRECT, new IntPtr(index), ref rect);
        var position = new IntPtr(NativeMethods.MakeLong(rect.left + 6, rect.top + Math.Max(1, rect.Height / 2)));
        NativeMethods.SendMessage(strip, NativeMethods.WM_MBUTTONDOWN, new IntPtr(0x0010), position);
        NativeMethods.SendMessage(strip, NativeMethods.WM_MBUTTONUP, IntPtr.Zero, position);
    }

    /// <summary>The active tab's close button sits inside that tab's own edges, however narrow it is.</summary>
    private void AssertCloseButtonOnActiveTab()
    {
        var closeButton = _window.GetControl(MainWindow.IdCloseActiveTab);
        NativeMethods.GetWindowRect(closeButton, out var closeBox);
        var tabStrip = _window.GetControl(MainWindow.IdTabs);
        var activeItem = new NativeMethods.RECT();
        NativeMethods.SendMessage(tabStrip, NativeMethods.TCM_GETITEMRECT,
            new IntPtr(_window.SelectedTabIndex), ref activeItem);
        NativeMethods.GetWindowRect(tabStrip, out var stripBox);
        if (closeBox.Width < 14 || closeBox.Height < 14)
        {
            throw new InvalidOperationException("The close button fell below a clickable size with " +
                _window.TabCount + " tabs open: " + closeBox.Width + "x" + closeBox.Height + ".");
        }

        // The strip has no border, so an item rectangle offsets straight from its screen origin.
        var closeCenter = (closeBox.left + closeBox.right) / 2;
        if (closeCenter < stripBox.left + activeItem.left || closeCenter > stripBox.left + activeItem.right)
        {
            throw new InvalidOperationException("The close button left the active tab with " +
                _window.TabCount + " tabs open.");
        }

        // The strip scrolled the active tab into view, so the close button is on screen and clear
        // of the new-tab button rather than hiding under it.
        NativeMethods.GetWindowRect(_window.GetControl(MainWindow.IdNewTab), out var newTabBox);
        if (closeBox.right > newTabBox.left)
        {
            throw new InvalidOperationException("The close button reaches past the tab band with " +
                _window.TabCount + " tabs open.");
        }
    }

    private void AssertActiveTabFinishedLoading()
    {
        var index = _window.SelectedTabIndex;
        if (index < 0) throw new InvalidOperationException("The native tab strip has no active item.");
        var text = _window.GetTabText(index);
        if (text.EndsWith("...", StringComparison.Ordinal))
            throw new InvalidOperationException("The active tab still shows a loading marker after navigation completed: " + text);
    }

}
#endif
