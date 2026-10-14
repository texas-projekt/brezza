using System;
using System.Collections.Generic;
using Microsoft.Win32;
using Brezza.Core.Navigation;

namespace Brezza.App.Native;

/// <summary>Whether a palette sits on the light or the dark system rendering of standard controls.</summary>
internal enum NativeColorScheme
{
    Light,
    Dark,
}

/// <summary>
/// A theme: the flat color roles shared by every native surface, and nothing else. Sizes, spacing,
/// radii, and fonts live in <see cref="NativeMetrics"/> and <see cref="NativeFonts"/>, so a theme
/// can only change colors.
/// </summary>
/// <remarks>
/// <see cref="Frame"/> sits behind the tab row, <see cref="Surface"/> is the toolbar, the active
/// tab, the new-tab page, and panels, and <see cref="Field"/> fills text inputs and lists. Every
/// color is a Win32 COLORREF (0x00BBGGRR); use <see cref="NativeMethods.Rgb"/> to write one.
/// </remarks>
internal sealed class NativeThemePalette
{
    /// <summary>The light palette.</summary>
    public static readonly NativeThemePalette Light = new()
    {
        Scheme = NativeColorScheme.Light,
        Frame = NativeMethods.Rgb(0xE8, 0xEB, 0xEF),
        Surface = NativeMethods.Rgb(0xFF, 0xFF, 0xFF),
        Field = NativeMethods.Rgb(0xF1, 0xF3, 0xF4),
        Hover = NativeMethods.Rgb(0xE3, 0xE6, 0xEA),
        Pressed = NativeMethods.Rgb(0xD5, 0xD9, 0xDE),
        Text = NativeMethods.Rgb(0x1F, 0x1F, 0x1F),
        SecondaryText = NativeMethods.Rgb(0x5F, 0x63, 0x68),
        DisabledText = NativeMethods.Rgb(0xB0, 0xB4, 0xB9),
        Border = NativeMethods.Rgb(0xD6, 0xDA, 0xDF),
        BorderHover = NativeMethods.Rgb(0xB4, 0xBA, 0xC2),
        Accent = NativeMethods.Rgb(0x00, 0x5F, 0xB8),
        AccentText = NativeMethods.Rgb(0xFF, 0xFF, 0xFF),
        Selection = NativeMethods.Rgb(0xDC, 0xEB, 0xFA),
        Notice = NativeMethods.Rgb(0xFF, 0xF4, 0xCE),
        NoticeText = NativeMethods.Rgb(0x4D, 0x3A, 0x00),
        Danger = NativeMethods.Rgb(0xC4, 0x2B, 0x1C),
        DangerText = NativeMethods.Rgb(0xFF, 0xFF, 0xFF),
    };

    /// <summary>The dark palette.</summary>
    public static readonly NativeThemePalette Dark = new()
    {
        Scheme = NativeColorScheme.Dark,
        Frame = NativeMethods.Rgb(0x1C, 0x1C, 0x1C),
        Surface = NativeMethods.Rgb(0x2B, 0x2B, 0x2B),
        Field = NativeMethods.Rgb(0x1F, 0x1F, 0x1F),
        Hover = NativeMethods.Rgb(0x3A, 0x3A, 0x3A),
        Pressed = NativeMethods.Rgb(0x46, 0x46, 0x46),
        Text = NativeMethods.Rgb(0xF3, 0xF3, 0xF3),
        SecondaryText = NativeMethods.Rgb(0xA8, 0xA8, 0xA8),
        DisabledText = NativeMethods.Rgb(0x6A, 0x6A, 0x6A),
        Border = NativeMethods.Rgb(0x3F, 0x3F, 0x3F),
        BorderHover = NativeMethods.Rgb(0x5C, 0x5C, 0x5C),
        Accent = NativeMethods.Rgb(0x4C, 0xC2, 0xFF),
        AccentText = NativeMethods.Rgb(0x00, 0x1A, 0x29),
        Selection = NativeMethods.Rgb(0x1E, 0x3A, 0x4F),
        Notice = NativeMethods.Rgb(0x43, 0x35, 0x19),
        NoticeText = NativeMethods.Rgb(0xF7, 0xE2, 0xA8),
        Danger = NativeMethods.Rgb(0xC4, 0x2B, 0x1C),
        DangerText = NativeMethods.Rgb(0xFF, 0xFF, 0xFF),
    };

    private NativeThemePalette() { }

    /// <summary>
    /// Which system rendering standard controls, scroll bars, tooltips, and the window frame use.
    /// Windows offers only light and dark variants of those, so this is the one non-color property.
    /// </summary>
    public NativeColorScheme Scheme { get; private set; }

    public bool IsDark => Scheme == NativeColorScheme.Dark;

    public uint Frame { get; private set; }
    public uint Surface { get; private set; }
    public uint Field { get; private set; }
    public uint Hover { get; private set; }
    public uint Pressed { get; private set; }
    public uint Text { get; private set; }
    public uint SecondaryText { get; private set; }
    public uint DisabledText { get; private set; }
    public uint Border { get; private set; }
    public uint BorderHover { get; private set; }
    public uint Accent { get; private set; }
    public uint AccentText { get; private set; }
    public uint Selection { get; private set; }
    public uint Notice { get; private set; }
    public uint NoticeText { get; private set; }
    public uint Danger { get; private set; }
    public uint DangerText { get; private set; }

    /// <summary>The color painted behind a control that sits on a backdrop role.</summary>
    public uint Backdrop(FlatBackdrop backdrop) => backdrop switch
    {
        FlatBackdrop.Frame => Frame,
        FlatBackdrop.Notice => Notice,
        _ => Surface,
    };

    /// <summary>The text color that reads on a backdrop role.</summary>
    public uint TextOn(FlatBackdrop backdrop) => backdrop == FlatBackdrop.Notice ? NoticeText : Text;
}

/// <summary>
/// The single source of the active theme. The shell owns one instance and gives it to every native
/// window, so the main window, panels, and pop-ups always resolve the same palette, and a theme
/// change reaches them through one <see cref="Changed"/> event instead of one push per window.
/// </summary>
internal sealed class NativeThemeSource
{
    public NativeThemeSource(BrowserThemeMode mode = BrowserThemeMode.System)
    {
        Mode = mode;
        Palette = NativeTheme.Resolve(mode);
    }

    public BrowserThemeMode Mode { get; private set; }

    public NativeThemePalette Palette { get; private set; }

    /// <summary>Raised after the palette changed. Handlers repaint; they never set the theme.</summary>
    public event EventHandler? Changed;

    /// <summary>Selects a mode; returns whether the resolved palette changed.</summary>
    public bool SetMode(BrowserThemeMode mode)
    {
        Mode = mode;
        return Resolve();
    }

    /// <summary>
    /// Re-resolves <see cref="BrowserThemeMode.System"/> after Windows reported a palette change;
    /// returns whether the resolved palette changed.
    /// </summary>
    public bool RefreshSystem() => Mode == BrowserThemeMode.System && Resolve();

    private bool Resolve()
    {
        var palette = NativeTheme.Resolve(Mode);
        if (ReferenceEquals(palette, Palette)) return false;
        Palette = palette;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}

/// <summary>
/// Per-window GDI resources for the active theme: cached solid brushes and the system rendering of
/// standard controls. One instance per window keeps brush lifetime with that window; the palette
/// itself always comes from the shared <see cref="NativeThemeSource"/>.
/// </summary>
internal sealed class NativeThemeResources : IDisposable
{
    private readonly NativeThemeSource _source;
    private readonly Dictionary<uint, IntPtr> _brushes = new();

    public NativeThemeResources(NativeThemeSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public NativeThemePalette Palette => _source.Palette;

    /// <summary>A cached solid brush for a color; owned by this instance.</summary>
    public IntPtr Brush(uint color)
    {
        if (_brushes.TryGetValue(color, out var brush)) return brush;
        brush = NativeMethods.CreateSolidBrush(color);
        _brushes[color] = brush;
        return brush;
    }

    /// <summary>Drops brushes created for the previous palette; call when the theme changed.</summary>
    public void ReleaseBrushes()
    {
        foreach (var brush in _brushes.Values) NativeMethods.DeleteObject(brush);
        _brushes.Clear();
    }

    /// <summary>Applies the dark or light system rendering to a top-level window frame.</summary>
    public void ApplyToWindowFrame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var dark = Palette.IsDark ? 1 : 0;
        // Older Windows builds do not know the attribute; the frame then stays light.
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        NativeMethods.SetWindowTheme(hwnd, SystemVisualClass(), null);
    }

    /// <summary>Applies the system dark rendering to a standard control (list, scroll bar, tooltip).</summary>
    public void ApplyToControl(IntPtr control)
    {
        if (control == IntPtr.Zero) return;
        NativeMethods.SetWindowTheme(control, SystemVisualClass(), null);
    }

    /// <summary>Applies the system dark rendering Windows uses for edits and combo boxes.</summary>
    public void ApplyToField(IntPtr control)
    {
        if (control == IntPtr.Zero) return;
        // DarkMode_CFD is the dark visual class Windows uses for combo boxes and edits.
        NativeMethods.SetWindowTheme(control, Palette.IsDark ? "DarkMode_CFD" : null, null);
    }

    public void Dispose() => ReleaseBrushes();

    private string? SystemVisualClass() => Palette.IsDark ? "DarkMode_Explorer" : null;
}

internal static class NativeTheme
{
    public static NativeThemePalette Resolve(BrowserThemeMode mode) => mode switch
    {
        BrowserThemeMode.Dark => NativeThemePalette.Dark,
        BrowserThemeMode.Light => NativeThemePalette.Light,
        _ => IsSystemDark() ? NativeThemePalette.Dark : NativeThemePalette.Light,
    };

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
