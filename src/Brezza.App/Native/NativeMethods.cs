using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Brezza.App.Native;

/// <summary>
/// Win32 declarations used by the native browser shell. The shell owns its own window, controls,
/// and message loop; no UI framework control abstractions are used.
/// </summary>
internal static class NativeMethods
{
    internal const string User32 = "user32.dll";
    internal const string ComCtl32 = "comctl32.dll";
    internal const string Gdi32 = "gdi32.dll";
    internal const string UxTheme = "uxtheme.dll";

    // ---------- Window styles ----------
    internal const uint WS_OVERLAPPED = 0x00000000;
    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_CAPTION = 0x00C00000;
    internal const uint WS_SYSMENU = 0x00080000;
    internal const uint WS_THICKFRAME = 0x00040000;
    internal const uint WS_MINIMIZEBOX = 0x00020000;
    internal const uint WS_MAXIMIZEBOX = 0x00010000;
    internal const uint WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    internal const uint WS_CHILD = 0x40000000;
    internal const uint WS_TABSTOP = 0x00010000;
    internal const uint WS_VISIBLE = 0x10000000;
    internal const uint WS_CLIPSIBLINGS = 0x04000000;
    internal const uint WS_CLIPCHILDREN = 0x02000000;
    internal const uint WS_VSCROLL = 0x00200000;
    internal const uint WS_BORDER = 0x00800000;
    internal const uint WS_EX_CONTROLPARENT = 0x00010000;
    internal const uint WS_EX_CLIENTEDGE = 0x00000200;
    internal const uint WS_EX_DLGMODALFRAME = 0x00000001;

    internal const uint SW_SHOW = 5;
    internal const uint SW_HIDE = 0;

    internal const int COLOR_WINDOW = 5;
    internal const int COLOR_BTNFACE = 15;

    // ---------- Messages ----------
    internal const uint WM_CREATE = 0x0001;
    internal const uint WM_NCHITTEST = 0x0084;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_PAINT = 0x000F;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_SETREDRAW = 0x000B;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_CHAR = 0x0102;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_INITDIALOG = 0x0110;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_CTLCOLORSTATIC = 0x0138;
    internal const uint WM_CTLCOLORBTN = 0x0135;
    internal const uint WM_CTLCOLOREDIT = 0x0133;
    internal const uint WM_CTLCOLORLISTBOX = 0x0134;
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_MBUTTONDOWN = 0x0207;
    internal const uint WM_MBUTTONUP = 0x0208;
    internal const uint WM_GETMINMAXINFO = 0x0024;
    internal const uint WM_DPICHANGED = 0x02E0;
    internal const uint WM_NOTIFY = 0x004E;
    internal const uint WM_SETFONT = 0x0030;
    internal const uint WM_GETFONT = 0x0031;
    internal const uint WM_NCCALCSIZE = 0x0083;
    internal const uint WM_APP = 0x8000;
    internal const uint WM_ACTIVATE = 0x0006;
    internal const uint WM_MOVE = 0x0003;
    internal const int WA_INACTIVE = 0;
    internal const uint LB_GETTOPINDEX = 0x018E;
    internal const uint LB_SETTOPINDEX = 0x0197;
    internal const int LBN_SELCHANGE = 1;
    internal const int IDOK = 1;
    internal const uint WM_CAPTURECHANGED = 0x0215;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_MOUSEACTIVATE = 0x0021;
    internal const int MA_NOACTIVATE = 3;
    internal const int VK_UP = 0x26;
    internal const int VK_DOWN = 0x28;
    internal const int VK_F4 = 0x73;
    internal const uint CS_DROPSHADOW = 0x00020000;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint SW_SHOWNOACTIVATE = 4;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    internal const uint WM_HSCROLL = 0x0114;
    internal const int SB_THUMBPOSITION = 4;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWCP_ROUNDSMALL = 3;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint LB_ITEMFROMPOINT = 0x01A9;
    internal const uint LB_GETITEMRECT = 0x0198;
    internal const uint TTS_NOANIMATE = 0x10;
    internal const uint TTS_NOFADE = 0x20;
    internal const uint WM_NCPAINT = 0x0085;
    internal const uint RDW_INVALIDATE = 0x0001;
    internal const uint RDW_FRAME = 0x0400;
    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_MOUSELEAVE = 0x02A3;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_SETFOCUS = 0x0007;
    internal const uint WM_KILLFOCUS = 0x0008;
    internal const uint WM_ENABLE = 0x000A;
    internal const int HTCLIENT = 1;
    internal const int HTCAPTION = 2;
    internal const int HTTOP = 12;

    internal const uint WHEEL_DELTA = 120;

    // ---------- Virtual keys ----------
    internal const int VK_RETURN = 0x0D;
    internal const int VK_ESCAPE = 0x1B;
    internal const int VK_TAB = 0x09;
    internal const int VK_F5 = 0x74;
    internal const int VK_LEFT = 0x25;
    internal const int VK_RIGHT = 0x27;
    internal const int VK_PRIOR = 0x21;
    internal const int VK_NEXT = 0x22;
    internal const int VK_END = 0x23;
    internal const int VK_HOME = 0x24;
    internal const int VK_DELETE = 0x2E;

    // ---------- Combo box ----------
    internal const uint CBS_DROPDOWN = 0x0002;
    internal const uint CBS_DROPDOWNLIST = 0x0003;
    internal const uint CBS_OWNERDRAWFIXED = 0x0010;
    internal const uint CBS_AUTOHSCROLL = 0x0040;
    internal const uint CBS_HASSTRINGS = 0x0200;
    internal const uint CB_ADDSTRING = 0x0143;
    internal const uint CB_RESETCONTENT = 0x014B;
    internal const uint CB_GETCURSEL = 0x0147;
    internal const uint CB_GETLBTEXT = 0x0148;
    internal const uint CB_GETLBTEXTLEN = 0x0149;
    internal const uint CB_GETCOUNT = 0x0146;
    internal const uint CB_SETCURSEL = 0x014E;
    internal const uint CB_LIMITTEXT = 0x0141;
    internal const uint CB_SETITEMHEIGHT = 0x0153;
    internal const uint CB_GETITEMHEIGHT = 0x0154;
    internal const uint CB_GETDROPPEDSTATE = 0x0157;
    internal const int CBN_CLOSEUP = 8;
    internal const int CBN_SELCHANGE = 1;
    internal const int CBN_DROPDOWN = 7;
    internal const int CBN_EDITCHANGE = 5;

    // ---------- Tab control ----------
    internal const uint TCS_TABS = 0x0000;
    internal const uint TCS_SINGLELINE = 0x0000;
    internal const uint TCS_FOCUSNEVER = 0x0008;
    internal const uint TCM_FIRST = 0x1300;
    internal const uint TCM_SETBKCOLOR = TCM_FIRST + 1;
    internal const uint TCM_SETTEXTCOLOR = TCM_FIRST + 9;
    internal const uint TCM_GETITEMCOUNT = TCM_FIRST + 4;
    internal const uint TCM_GETITEM = TCM_FIRST + 60;
    internal const uint TCM_SETITEM = TCM_FIRST + 61;
    internal const uint TCM_INSERTITEM = TCM_FIRST + 62;
    internal const uint TCM_DELETEITEM = TCM_FIRST + 8;
    internal const uint TCM_DELETEALLITEMS = TCM_FIRST + 9;
    internal const uint TCM_SETCURSEL = TCM_FIRST + 12;
    internal const uint TCM_GETCURSEL = TCM_FIRST + 11;
    internal const uint TCM_HITTEST = TCM_FIRST + 13;
    internal const uint TCM_GETITEMRECT = TCM_FIRST + 10;
    internal const uint TCM_ADJUSTRECT = TCM_FIRST + 40;
    internal const uint TCM_SETITEMSIZE = TCM_FIRST + 41;
    internal const uint TCS_FIXEDWIDTH = 0x0400;
    internal const uint TCIF_TEXT = 0x0001;
    internal const uint TCIF_PARAM = 0x0008;
    internal const int TCN_FIRST = -550;
    internal const int TCN_SELCHANGE = TCN_FIRST - 1;
    internal const int TCN_SELCHANGING = TCN_FIRST - 2;

    // ---------- List box ----------
    internal const uint LBS_NOTIFY = 0x0001;
    internal const uint LBS_HASSTRINGS = 0x0040;
    internal const uint LBS_USETABSTOPS = 0x0080;
    internal const uint LBS_OWNERDRAWFIXED = 0x0010;
    internal const uint LBS_NOINTEGRALHEIGHT = 0x0100;
    internal const uint LB_SETITEMHEIGHT = 0x01A0;
    internal const uint LB_ADDSTRING = 0x0180;
    internal const uint LB_GETCURSEL = 0x0188;
    internal const uint LB_SETCURSEL = 0x0186;
    internal const uint LB_RESETCONTENT = 0x0184;
    internal const uint LB_GETCOUNT = 0x018B;
    internal const int LBN_DBLCLK = 2;
    // ---------- Button ----------
    internal const uint BS_PUSHBUTTON = 0x00000000;
    internal const uint BS_OWNERDRAW = 0x0000000B;
    internal const uint BS_TEXT = 0x00000000;
    internal const int BN_CLICKED = 0;
    internal const uint ODS_SELECTED = 0x0001;
    internal const uint ODS_DISABLED = 0x0004;
    internal const uint ODS_FOCUS = 0x0010;
    internal const uint ODS_NOFOCUSRECT = 0x0200;
    internal const uint ODT_LISTBOX = 2;
    internal const uint ODT_COMBOBOX = 3;
    internal const uint ODS_COMBOBOXEDIT = 0x1000;
    internal const uint ODT_BUTTON = 4;

    // ---------- Tooltip ----------
    internal const string TooltipClass = "tooltips_class32";
    internal const uint TTS_ALWAYSTIP = 0x01;
    internal const uint TTS_NOPREFIX = 0x02;
    internal const uint TTF_IDISHWND = 0x0001;
    internal const uint TTF_SUBCLASS = 0x0010;
    internal const uint TTM_ADDTOOL = 0x0432;
    internal const uint TTM_UPDATETIPTEXT = 0x0439;
    internal const uint TTM_SETMAXTIPWIDTH = 0x0418;
    internal const uint WS_EX_TOPMOST = 0x00000008;

    // ---------- Mouse tracking, metrics, DWM ----------
    internal const uint TME_LEAVE = 0x00000002;
    internal const int SM_CYFRAME = 33;
    internal const int SM_CXPADDEDBORDER = 92;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const uint SW_MINIMIZE = 6;
    internal const uint SW_MAXIMIZE = 3;
    internal const uint SW_RESTORE = 9;

    // ---------- Static ----------
    internal const uint SS_LEFT = 0x00000000;
    internal const uint SS_NOPREFIX = 0x00000080;
    internal const uint SS_ENDELLIPSIS = 0x00004000;
    internal const uint SS_LEFTNOWORDWRAP = 0x0000000C;
    internal const uint SS_CENTER = 0x00000001;


    // ---------- Edit ----------
    internal const uint ES_AUTOHSCROLL = 0x0080;
    internal const uint ES_MULTILINE = 0x0004;
    internal const uint ES_READONLY = 0x0800;
    internal const int EN_CHANGE = 0x0300;
    internal const int EN_SETFOCUS = 0x0100;

    // ---------- Window access ----------
    internal const int GWLP_WNDPROC = -4;
    internal const int GWLP_USERDATA = -21;
    internal const int GWLP_ID = -12;
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    // ---------- SetWindowPos ----------
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal static readonly IntPtr HWND_BOTTOM = new(1);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    // ---------- Drawing ----------
    internal const uint DT_LEFT = 0x00000000;
    internal const uint DT_CENTER = 0x00000001;
    internal const uint DT_VCENTER = 0x00000004;
    internal const uint DT_BOTTOM = 0x00000008;
    internal const uint DT_WORDBREAK = 0x00000010;
    internal const uint DT_SINGLELINE = 0x00000020;
    internal const uint DT_NOPREFIX = 0x00000800;
    internal const uint DT_END_ELLIPSIS = 0x00008000;
    internal const uint TRANSPARENT = 1;
    internal const int LOGPIXELSY = 90;
    internal const int DEFAULT_QUALITY = 0;
    internal const int OUT_TT_PRECIS = 4;
    internal const int CLIP_DEFAULT_PRECIS = 0;
    internal const byte DEFAULT_PITCH = 0;
    internal const byte FF_DONTCARE = 0;
    internal const int FW_NORMAL = 400;
    internal const int FW_SEMIBOLD = 600;
    internal const byte DEFAULT_CHARSET = 1;
    internal const uint USER_DEFAULT_SCREEN_DPI = 96;

    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;

    internal const uint CB_SETEDITSEL = 0x0142;
    internal const uint EM_SETSEL = 0x00B1;
    internal const uint CB_SHOWDROPDOWN = 0x014F;
    internal const uint TCS_TOOLTIPS = 0x4000;

    internal const uint MB_OK = 0x00000000;
    internal const uint MB_ICONERROR = 0x00000010;
    internal const uint MB_ICONWARNING = 0x00000030;
    internal const uint MB_YESNO = 0x00000004;
    internal const uint MB_ICONQUESTION = 0x00000020;
    internal const int IDYES = 6;
    internal const int IDCANCEL = 2;

    internal const uint ERROR_CLASS_ALREADY_EXISTS = 1410;

    // ---------- Delegates ----------
    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal delegate IntPtr SubClassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData);

    // ---------- Structures ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;

        public int Width => right - left;

        public int Height => bottom - top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TCITEM
    {
        public uint mask;
        public int dwState;
        public int dwStateMask;
        public string pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
    }

    /// <summary>Tab item whose text buffer is caller-allocated native memory.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TCITEM_BUFFER
    {
        public uint mask;
        public int dwState;
        public int dwStateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TCHITTESTINFO
    {
        public POINT pt;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NMHDR
    {
        public IntPtr hwndFrom;
        public UIntPtr idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DRAWITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemAction, itemState;
        public IntPtr hwndItem, hDC;
        public RECT rcItem;
        public IntPtr itemData;
    }




    [StructLayout(LayoutKind.Sequential)]
    internal struct MEASUREITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemWidth, itemHeight;
        public IntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TOOLINFO
    {
        public uint cbSize;
        public uint uFlags;
        public IntPtr hwnd;
        public IntPtr uId;
        public RECT rect;
        public IntPtr hinst;
        public string lpszText;
        public IntPtr lParam;
        public IntPtr lpReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COMBOBOXINFO
    {
        public uint cbSize;
        public RECT rcItem;
        public RECT rcButton;
        public uint stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndItem;
        public IntPtr hwndList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    // ---------- kernel32 ----------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    // ---------- user32 ----------
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX wndClass);
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, uint command);

    [DllImport(User32)]
    internal static extern bool IsZoomed(IntPtr hWnd);


    [DllImport(User32)]
    internal static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);


    [DllImport(User32)]
    internal static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport(User32)]
    internal static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);


    [DllImport(User32)]
    internal static extern bool TranslateMessage(ref MSG msg);

    [DllImport(User32)]
    internal static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport(User32)]
    internal static extern void PostQuitMessage(int exitCode);


    [DllImport(User32)]
    internal static extern IntPtr GetActiveWindow();

    [DllImport(User32)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);


    [DllImport(User32)]
    internal static extern IntPtr SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport(User32)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);


    [DllImport(User32)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);



    [DllImport(User32, SetLastError = true)]
    internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport(User32, SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(
        IntPtr parent, IntPtr childAfter, string className, string? windowText);

    [DllImport(User32)]
    internal static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetWindowText(IntPtr hWnd, string text);

    [DllImport(User32)]
    internal static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport(User32)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport(UxTheme, CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);


    [DllImport(User32)]
    internal static extern IntPtr SetTimer(IntPtr hWnd, IntPtr idEvent, uint elapsedMillis, IntPtr timerProc);

    [DllImport(User32)]
    internal static extern bool KillTimer(IntPtr hWnd, IntPtr idEvent);

    [DllImport(User32)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);


    [DllImport(User32)]
    internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport(User32)]
    internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);





    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport(User32)]
    internal static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    [DllImport(User32)]
    internal static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    [DllImport(User32)]
    internal static extern short GetKeyState(int virtualKey);

    [DllImport(User32)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);


    [DllImport(User32)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref TCITEM item);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref TCHITTESTINFO info);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref RECT rect);

    [DllImport(User32)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);




    // ---------- gdi32 ----------
    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFontIndirect(ref LOGFONT font);

    [DllImport(Gdi32, SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr handle);

    [DllImport(Gdi32)]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport(Gdi32)]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport(Gdi32)]
    internal static extern bool SetTextColor(IntPtr dc, uint color);

    [DllImport(Gdi32)]
    internal static extern bool SetBkColor(IntPtr dc, uint color);

    [DllImport(Gdi32)]
    internal static extern bool SetBkMode(IntPtr dc, int mode);


    [DllImport(Gdi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out POINT size);

    [DllImport(User32, CharSet = CharSet.Unicode, EntryPoint = "DrawTextExW")]
    internal static extern int DrawTextEx(
        IntPtr dc, string text, int length, ref RECT rect, uint format, IntPtr dtp);




    // ---------- comctl32 ----------
    [DllImport(ComCtl32, CallingConvention = CallingConvention.StdCall)]
    internal static extern bool SetWindowSubclass(
        IntPtr hWnd, SubClassProc proc, UIntPtr id, IntPtr referenceData);

    [DllImport(ComCtl32, CallingConvention = CallingConvention.StdCall)]
    internal static extern bool RemoveWindowSubclass(IntPtr hWnd, SubClassProc proc, UIntPtr id);

    [DllImport(ComCtl32, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport(User32, SetLastError = true)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref TOOLINFO info);

    [DllImport(User32)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern IntPtr GetFocus();

    [DllImport(User32)]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern int GetDlgCtrlID(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool IsDialogMessage(IntPtr dialog, ref MSG message);

    [DllImport(User32)]
    internal static extern bool IsChild(IntPtr parent, IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool ReleaseCapture();

    [DllImport(User32)]
    internal static extern IntPtr GetCapture();

    [DllImport(User32)]
    internal static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport(User32)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport(User32)]
    internal static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport(User32)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport(User32)]
    internal static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport(Gdi32, CharSet = CharSet.Unicode)]
    internal static extern int GetTextFace(IntPtr dc, int count, System.Text.StringBuilder faceName);

    [DllImport(Gdi32)]
    internal static extern int ExcludeClipRect(IntPtr dc, int left, int top, int right, int bottom);

    [DllImport(User32)]
    internal static extern bool GetComboBoxInfo(IntPtr combo, ref COMBOBOXINFO info);

    [DllImport(User32)]
    internal static extern bool InvalidateRect(IntPtr hWnd, ref RECT rect, bool erase);

    [DllImport(User32)]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT eventTrack);

    [DllImport(User32)]
    internal static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport(User32)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport(Gdi32)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport(Gdi32)]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport(Gdi32)]
    internal static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport(Gdi32)]
    internal static extern bool DeleteDC(IntPtr dc);

    internal const uint SRCCOPY = 0x00CC0020;
    internal const uint TCM_GETTOOLTIPS = TCM_FIRST + 45;

    [DllImport(Gdi32)]
    internal static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport(Gdi32)]
    internal static extern bool FillRgn(IntPtr dc, IntPtr region, IntPtr brush);

    [DllImport(Gdi32)]
    internal static extern bool FrameRgn(IntPtr dc, IntPtr region, IntPtr brush, int width, int height);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    // ---------- helpers ----------
    internal static int GetHiWord(IntPtr value) => unchecked((int)((long)value >> 16)) & 0xFFFF;

    internal static int GetLoWord(IntPtr value) => unchecked((int)(long)value) & 0xFFFF;

    internal static int GetLoWord(UIntPtr value) => unchecked((int)(ulong)value) & 0xFFFF;

    internal static int MakeLong(int low, int high) => (high << 16) | (low & 0xFFFF);

    internal static uint Rgb(byte red, byte green, byte blue) => red | (uint)(green << 8) | (uint)(blue << 16);

    /// <summary>Reads the text of a tab-control item through a caller-allocated native buffer.</summary>
    internal static string GetTabText(IntPtr tabStrip, int index)
    {
        const int capacity = 256;
        var item = Marshal.AllocHGlobal(Marshal.SizeOf<TCITEM_BUFFER>());
        var text = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            var request = new TCITEM_BUFFER
            {
                mask = TCIF_TEXT,
                pszText = text,
                cchTextMax = capacity,
            };
            Marshal.StructureToPtr(request, item, false);
            SendMessage(tabStrip, TCM_GETITEM, new IntPtr(index), item);
            return Marshal.PtrToStringUni(text) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            Marshal.FreeHGlobal(item);
        }
    }

    /// <summary>Reads a window's text into a string.</summary>
    internal static string GetWindowText(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        GetWindowText(hWnd, builder, builder.Capacity);
        return builder.ToString();
    }

    /// <summary>Reads the text of a combo-box list item.</summary>
    internal static string GetComboItemText(IntPtr combo, int index)
    {
        var length = (int)SendMessage(combo, CB_GETLBTEXTLEN, (IntPtr)index, IntPtr.Zero);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        SendMessage(combo, CB_GETLBTEXT, (IntPtr)index, builder);
        return builder.ToString();
    }

    internal static void SetWindowFont(IntPtr hWnd, IntPtr font, bool redraw)
    {
        SendMessage(hWnd, WM_SETFONT, font, redraw ? new IntPtr(1) : IntPtr.Zero);
    }

    internal static int Scale(int value, uint dpi) => (int)Math.Round(value * dpi / (double)USER_DEFAULT_SCREEN_DPI);
}
