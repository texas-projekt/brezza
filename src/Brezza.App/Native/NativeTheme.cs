using System;
using System.Collections.Generic;
using Microsoft.Win32;
using Brezza.Core.Navigation;

namespace Brezza.App.Native;

/// <summary>
/// The flat color roles shared by every native surface. <see cref="Frame"/> sits behind the tab row,
/// <see cref="Surface"/> is the toolbar, the active tab, the new-tab page, and panels, and
/// <see cref="Field"/> fills text inputs and lists.
/// </summary>
internal readonly struct NativeThemePalette
{
    public NativeThemePalette(bool dark)
    {
        Dark = dark;
        Frame = dark ? NativeMethods.Rgb(0x1C, 0x1C, 0x1C) : NativeMethods.Rgb(0xE8, 0xEB, 0xEF);
        Surface = dark ? NativeMethods.Rgb(0x2B, 0x2B, 0x2B) : NativeMethods.Rgb(0xFF, 0xFF, 0xFF);
        Field = dark ? NativeMethods.Rgb(0x1F, 0x1F, 0x1F) : NativeMethods.Rgb(0xF1, 0xF3, 0xF4);
        Hover = dark ? NativeMethods.Rgb(0x3A, 0x3A, 0x3A) : NativeMethods.Rgb(0xE3, 0xE6, 0xEA);
        Pressed = dark ? NativeMethods.Rgb(0x46, 0x46, 0x46) : NativeMethods.Rgb(0xD5, 0xD9, 0xDE);
        Text = dark ? NativeMethods.Rgb(0xF3, 0xF3, 0xF3) : NativeMethods.Rgb(0x1F, 0x1F, 0x1F);
        SecondaryText = dark ? NativeMethods.Rgb(0xA8, 0xA8, 0xA8) : NativeMethods.Rgb(0x5F, 0x63, 0x68);
        DisabledText = dark ? NativeMethods.Rgb(0x6A, 0x6A, 0x6A) : NativeMethods.Rgb(0xB0, 0xB4, 0xB9);
        Border = dark ? NativeMethods.Rgb(0x3F, 0x3F, 0x3F) : NativeMethods.Rgb(0xD6, 0xDA, 0xDF);
        BorderHover = dark ? NativeMethods.Rgb(0x5C, 0x5C, 0x5C) : NativeMethods.Rgb(0xB4, 0xBA, 0xC2);
        Accent = dark ? NativeMethods.Rgb(0x4C, 0xC2, 0xFF) : NativeMethods.Rgb(0x00, 0x5F, 0xB8);
        AccentText = dark ? NativeMethods.Rgb(0x00, 0x1A, 0x29) : NativeMethods.Rgb(0xFF, 0xFF, 0xFF);
        Selection = dark ? NativeMethods.Rgb(0x1E, 0x3A, 0x4F) : NativeMethods.Rgb(0xDC, 0xEB, 0xFA);
        Notice = dark ? NativeMethods.Rgb(0x43, 0x35, 0x19) : NativeMethods.Rgb(0xFF, 0xF4, 0xCE);
        NoticeText = dark ? NativeMethods.Rgb(0xF7, 0xE2, 0xA8) : NativeMethods.Rgb(0x4D, 0x3A, 0x00);
        Danger = NativeMethods.Rgb(0xC4, 0x2B, 0x1C);
        DangerText = NativeMethods.Rgb(0xFF, 0xFF, 0xFF);
    }

    public bool Dark { get; }
    public uint Frame { get; }
    public uint Surface { get; }
    public uint Field { get; }
    public uint Hover { get; }
    public uint Pressed { get; }
    public uint Text { get; }
    public uint SecondaryText { get; }
    public uint DisabledText { get; }
    public uint Border { get; }
    public uint BorderHover { get; }
    public uint Accent { get; }
    public uint AccentText { get; }
    public uint Selection { get; }
    public uint Notice { get; }
    public uint NoticeText { get; }
    public uint Danger { get; }
    public uint DangerText { get; }
}

/// <summary>
/// The active palette plus the GDI brushes created for it. One instance per window keeps brush
/// lifetime with that window; switching theme drops every cached brush.
/// </summary>
internal sealed class NativeThemeResources : IDisposable
{
    private readonly Dictionary<uint, IntPtr> _brushes = new();

    public NativeThemeResources(BrowserThemeMode mode = BrowserThemeMode.System)
    {
        Apply(mode);
    }

    public BrowserThemeMode Mode { get; private set; }

    public NativeThemePalette Palette { get; private set; }

    public void Apply(BrowserThemeMode mode)
    {
        Mode = mode;
        Palette = NativeTheme.Resolve(mode);
        ReleaseBrushes();
    }

    /// <summary>A cached solid brush for a color; owned by this instance.</summary>
    public IntPtr Brush(uint color)
    {
        if (_brushes.TryGetValue(color, out var brush)) return brush;
        brush = NativeMethods.CreateSolidBrush(color);
        _brushes[color] = brush;
        return brush;
    }

    /// <summary>Applies the dark or light system rendering to a top-level window frame.</summary>
    public void ApplyToWindowFrame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var dark = Palette.Dark ? 1 : 0;
        // Older Windows builds do not know the attribute; the frame then stays light.
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        NativeMethods.SetWindowTheme(hwnd, Palette.Dark ? "DarkMode_Explorer" : null, null);
    }

    /// <summary>Applies the system dark rendering to a standard control (combo box, edit, list, scroll bar).</summary>
    public void ApplyToControl(IntPtr control, bool field = false)
    {
        if (control == IntPtr.Zero) return;
        // DarkMode_CFD is the dark visual class Windows uses for combo boxes and edits.
        NativeMethods.SetWindowTheme(control, Palette.Dark ? (field ? "DarkMode_CFD" : "DarkMode_Explorer") : null, null);
    }

    public void Dispose() => ReleaseBrushes();

    private void ReleaseBrushes()
    {
        foreach (var brush in _brushes.Values) NativeMethods.DeleteObject(brush);
        _brushes.Clear();
    }
}

internal static class NativeTheme
{
    public static NativeThemePalette Resolve(BrowserThemeMode mode)
    {
        var dark = mode switch
        {
            BrowserThemeMode.Dark => true,
            BrowserThemeMode.Light => false,
            _ => IsSystemDark(),
        };
        return new NativeThemePalette(dark);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
