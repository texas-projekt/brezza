using System;
using System.Diagnostics;

namespace Brezza.App.Native;

/// <summary>
/// The DPI-scaled font set shared by the native windows: body text, headings, and the icon font
/// used for toolbar and caption glyphs. Icons use Segoe Fluent Icons (Windows 11) and fall back to
/// Segoe MDL2 Assets, which shares its code points, where Fluent Icons is not installed.
/// </summary>
internal sealed class NativeFonts : IDisposable
{
    public IntPtr Body { get; private set; }

    public IntPtr Strong { get; private set; }

    public IntPtr Heading { get; private set; }

    public IntPtr Display { get; private set; }

    public IntPtr Icon { get; private set; }

    public IntPtr SmallIcon { get; private set; }

    private static string? _iconFace;

    /// <summary>The installed icon font face; resolved once because installed fonts do not change while running.</summary>
    private static string IconFace => _iconFace ??= IsInstalled("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>Recreates every font for a DPI. Controls must be given the new handles afterwards.</summary>
    public void Create(uint dpi)
    {
        Dispose();
        Body = CreateFont("Segoe UI", 9, NativeMethods.FW_NORMAL, dpi);
        Strong = CreateFont("Segoe UI", 9, NativeMethods.FW_SEMIBOLD, dpi);
        Heading = CreateFont("Segoe UI", 15, NativeMethods.FW_SEMIBOLD, dpi);
        Display = CreateFont("Segoe UI", 22, NativeMethods.FW_SEMIBOLD, dpi);
        Icon = CreateFont(IconFace, 10.5, NativeMethods.FW_NORMAL, dpi);
        SmallIcon = CreateFont(IconFace, 7.5, NativeMethods.FW_NORMAL, dpi);
    }

    public void Dispose()
    {
        foreach (var font in new[] { Body, Strong, Heading, Display, Icon, SmallIcon })
        {
            if (font != IntPtr.Zero) NativeMethods.DeleteObject(font);
        }

        Body = Strong = Heading = Display = Icon = SmallIcon = IntPtr.Zero;
    }

    /// <summary>Measures single-line text in a font on a window's device context.</summary>
    public static NativeMethods.POINT Measure(IntPtr hwnd, IntPtr font, string text)
    {
        if (string.IsNullOrEmpty(text)) return default;
        var dc = NativeMethods.GetDC(hwnd);
        if (dc == IntPtr.Zero) return default;
        try
        {
            var previous = NativeMethods.SelectObject(dc, font);
            NativeMethods.GetTextExtentPoint32(dc, text, text.Length, out var size);
            NativeMethods.SelectObject(dc, previous);
            return size;
        }
        finally
        {
            NativeMethods.ReleaseDC(hwnd, dc);
        }
    }

    /// <summary>
    /// GDI silently substitutes another face for a missing font, so a font is installed only when
    /// the face it actually selects has the requested name.
    /// </summary>
    private static bool IsInstalled(string face)
    {
        var font = CreateFont(face, 9, NativeMethods.FW_NORMAL, NativeMethods.USER_DEFAULT_SCREEN_DPI);
        if (font == IntPtr.Zero) return false;
        var dc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var previous = NativeMethods.SelectObject(dc, font);
            var selected = new System.Text.StringBuilder(64);
            NativeMethods.GetTextFace(dc, selected.Capacity, selected);
            NativeMethods.SelectObject(dc, previous);
            return string.Equals(selected.ToString(), face, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, dc);
            NativeMethods.DeleteObject(font);
        }
    }

    private static IntPtr CreateFont(string face, double pointSize, int weight, uint dpi)
    {
        var font = new NativeMethods.LOGFONT
        {
            lfHeight = -(int)Math.Round(pointSize * dpi / 72.0),
            lfWeight = weight,
            lfCharSet = NativeMethods.DEFAULT_CHARSET,
            lfQuality = 5, // CLEARTYPE_QUALITY
            lfPitchAndFamily = NativeMethods.DEFAULT_PITCH | NativeMethods.FF_DONTCARE,
            lfFaceName = face,
        };
        var handle = NativeMethods.CreateFontIndirect(ref font);
        if (handle == IntPtr.Zero)
        {
            // A missing font must not stop the shell; Win32 falls back to the system font.
            Trace.TraceWarning("The font {0} could not be created; using the system font.", face);
        }

        return handle;
    }
}
