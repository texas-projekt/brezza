using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Tabs;

namespace Brezza.App;

/// <summary>Subclasses of the tab strip and the address field: mouse, wheel, and address editing.</summary>
internal sealed partial class MainWindow
{
    // ---------- Subclasses ----------

    private IntPtr OnTabStripMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        try
        {
            return OnTabStripMessageCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception exception)
        {
            // The tab strip is a system control; an exception must not escape its subclass.
            Trace.TraceError("The tab strip message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnTabStripMessageCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_PAINT:
                PaintTabStrip(hWnd);
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEMOVE:
                var hot = HitTestTab(lParam);
                if (hot != _hotTab)
                {
                    _hotTab = hot;
                    var track = new NativeMethods.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                        dwFlags = NativeMethods.TME_LEAVE,
                        hwndTrack = hWnd,
                    };
                    NativeMethods.TrackMouseEvent(ref track);
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                }

                break;

            case NativeMethods.WM_MOUSELEAVE:
                _hotTab = -1;
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                break;

            case NativeMethods.WM_MBUTTONDOWN:
                // One click closes one tab: the button-up message is swallowed, not acted on.
                var index = HitTestTab(lParam);
                if (index >= 0)
                {
                    CloseTabRequested?.Invoke(this, _tabMirror[index].Id);
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_MBUTTONUP:
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEWHEEL:
                var wheelDelta = unchecked((short)NativeMethods.GetHiWord(wParam));
                ScrollTabs(wheelDelta > 0 ? -1 : 1);
                return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private int HitTestTab(IntPtr lParam)
    {
        var info = new NativeMethods.TCHITTESTINFO
        {
            pt = new NativeMethods.POINT
            {
                x = unchecked((short)NativeMethods.GetLoWord(lParam)),
                y = unchecked((short)NativeMethods.GetHiWord(lParam)),
            },
        };
        var index = (int)NativeMethods.SendMessage(_tabStrip, NativeMethods.TCM_HITTEST, IntPtr.Zero, ref info);
        return index >= 0 && index < _tabMirror.Count ? index : -1;
    }

    private void ScrollTabs(int step)
    {
        var current = SelectedTabIndex;
        var next = current + step;
        if (next < 0 || next >= _tabMirror.Count) return;

        // Ask Core to activate the tab and let the next state render move the native selection.
        // Moving the selection first would make the view show a tab Core never accepted (for
        // example one that is closing).
        ActivateTabRequested?.Invoke(this, _tabMirror[next].Id);
    }

    private IntPtr OnAddressEditMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        try
        {
            return OnAddressEditMessageCore(hWnd, msg, wParam, lParam);
        }
        catch (Exception exception)
        {
            Trace.TraceError("The address edit message {0} failed: {1}", msg, exception);
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr OnAddressEditMessageCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_KEYDOWN:
                if (NativeMethods.GetLoWord(wParam) == NativeMethods.VK_RETURN)
                {
                    SubmitAddress();
                    return IntPtr.Zero;
                }

                if (NativeMethods.GetLoWord(wParam) == NativeMethods.VK_ESCAPE)
                {
                    RevertAddress();
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_CHAR:
                if (NativeMethods.GetLoWord(wParam) == NativeMethods.VK_RETURN) return IntPtr.Zero;
                break;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void SubmitAddress()
    {
        var text = NativeMethods.GetWindowText(_addressCombo);
        if (!string.IsNullOrWhiteSpace(text))
        {
            // The field now represents the committed address, so later state renders may update it.
            _addressEdited = false;
            NavigateRequested?.Invoke(this, text);
        }
    }

    private void RevertAddress()
    {
        _addressEdited = false;
        SetAddressTextCore(_currentUrl);
        NativeMethods.SendMessage(
            _addressEdit != IntPtr.Zero ? _addressEdit : _addressCombo,
            NativeMethods.EM_SETSEL,
            IntPtr.Zero,
            new IntPtr(-1));
    }
}
