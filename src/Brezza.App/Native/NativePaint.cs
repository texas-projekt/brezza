using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Brezza.App.Native;

/// <summary>Small GDI and GDI+ drawing helpers for flat, owner-painted surfaces.</summary>
internal static class NativePaint
{
    public static void Fill(IntPtr dc, NativeMethods.RECT rect, uint color, NativeThemeResources theme)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        NativeMethods.FillRect(dc, ref rect, theme.Brush(color));
    }

    /// <summary>
    /// Fills an anti-aliased rounded rectangle. GDI regions are cut on whole pixels, so rounded
    /// shapes go through GDI+; square corners stay on the cheaper GDI path.
    /// </summary>
    public static void FillRounded(IntPtr dc, NativeMethods.RECT rect, int radius, uint color, NativeThemeResources theme)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        if (radius <= 0)
        {
            Fill(dc, rect, color, theme);
            return;
        }

        using var lease = NativePaintScope.Lease(dc);
        using var path = RoundedPath(rect.left, rect.top, rect.Width, rect.Height, radius);
        using var brush = new SolidBrush(ToColor(color));
        lease.Graphics.FillPath(brush, path);
    }

    /// <summary>Strokes a crisp, anti-aliased 1px rounded border inside the rectangle.</summary>
    public static void FrameRounded(IntPtr dc, NativeMethods.RECT rect, int radius, uint color, NativeThemeResources theme)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return;
        using var lease = NativePaintScope.Lease(dc);
        // A 1px pen centered half a pixel inside the edge covers exactly one pixel row instead of
        // smearing across two.
        using var path = RoundedPath(rect.left + 0.5f, rect.top + 0.5f, rect.Width - 1, rect.Height - 1, Math.Max(0, radius - 0.5f));
        using var pen = new Pen(ToColor(color), 1f);
        lease.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(width, height));
        if (diameter <= 0)
        {
            path.AddRectangle(new RectangleF(x, y, width, height));
            return path;
        }

        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Converts a GDI COLORREF (0x00BBGGRR) to an opaque GDI+ color.</summary>
    private static Color ToColor(uint colorRef) =>
        Color.FromArgb((int)(colorRef & 0xFF), (int)((colorRef >> 8) & 0xFF), (int)((colorRef >> 16) & 0xFF));

    public static void Text(IntPtr dc, string text, NativeMethods.RECT rect, IntPtr font, uint color, uint flags)
    {
        if (string.IsNullOrEmpty(text)) return;
        var previous = NativeMethods.SelectObject(dc, font);
        NativeMethods.SetBkMode(dc, (int)NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(dc, color);
        NativeMethods.DrawTextEx(dc, text, -1, ref rect, flags | NativeMethods.DT_NOPREFIX, IntPtr.Zero);
        NativeMethods.SelectObject(dc, previous);
    }

    public static NativeMethods.RECT Rect(int x, int y, int width, int height) =>
        new() { left = x, top = y, right = x + width, bottom = y + height };

    /// <summary>
    /// Draws an image with alpha, scaled into the rectangle. A favicon is small and fetched at its
    /// natural size, so it is stretched to the slot the tab strip reserves for it.
    /// </summary>
    public static void Image(IntPtr dc, Bitmap image, NativeMethods.RECT rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var lease = NativePaintScope.Lease(dc);
        lease.Graphics.DrawImage(image, new Rectangle(rect.left, rect.top, rect.Width, rect.Height));
    }
}

/// <summary>
/// Paints into an off-screen bitmap and copies it to the target on dispose, so owner-painted
/// surfaces that repaint often (tab strip, fields) do not flicker.
/// </summary>
internal readonly struct NativeBufferedPaint : IDisposable
{
    private readonly IntPtr _target;
    private readonly NativeMethods.RECT _bounds;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previous;

    public NativeBufferedPaint(IntPtr target, NativeMethods.RECT bounds)
    {
        _target = target;
        _bounds = bounds;
        Dc = NativeMethods.CreateCompatibleDC(target);
        _bitmap = NativeMethods.CreateCompatibleBitmap(target, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        _previous = NativeMethods.SelectObject(Dc, _bitmap);
    }

    public IntPtr Dc { get; }

    public void Dispose()
    {
        NativeMethods.BitBlt(_target, _bounds.left, _bounds.top, _bounds.Width, _bounds.Height, Dc, _bounds.left, _bounds.top, NativeMethods.SRCCOPY);
        NativeMethods.SelectObject(Dc, _previous);
        NativeMethods.DeleteObject(_bitmap);
        NativeMethods.DeleteDC(Dc);
    }
}

/// <summary>
/// One GDI+ <see cref="System.Drawing.Graphics"/> shared by every rounded shape in a paint pass.
/// Creating a Graphics for a device context has a fixed cost, so a pass that draws several rounded
/// shapes opens a scope once; shapes drawn outside a scope get a short-lived Graphics of their own.
/// </summary>
/// <remarks>
/// Set <c>BREZZA_TRACE_PAINT=1</c> together with <c>BREZZA_TRACE_LOG</c> to trace how long each
/// scoped paint pass takes.
/// </remarks>
internal sealed class NativePaintScope : IDisposable
{
    private static readonly bool TracePaint =
        string.Equals(Environment.GetEnvironmentVariable("BREZZA_TRACE_PAINT"), "1", StringComparison.Ordinal);

    // Paint passes nest (a WM_DRAWITEM can arrive while a parent paints), so scopes form a stack.
    [ThreadStatic] private static NativePaintScope? _current;

    private readonly IntPtr _dc;
    private readonly NativePaintScope? _previous;
    private readonly string _name;
    private readonly Stopwatch? _timer;
    private Graphics? _graphics;

    private NativePaintScope(IntPtr dc, string name)
    {
        _dc = dc;
        _name = name;
        _previous = _current;
        _timer = TracePaint ? Stopwatch.StartNew() : null;
        _current = this;
    }

    /// <summary>Starts a paint pass on a device context; dispose it when the pass ends.</summary>
    public static NativePaintScope Begin(IntPtr dc, string name) => new(dc, name);

    /// <summary>The Graphics for a device context: the open scope's when it matches, otherwise a new one.</summary>
    public static PaintLease Lease(IntPtr dc)
    {
        if (_current is { } scope && scope._dc == dc) return new PaintLease(scope.Graphics, owned: false);
        return new PaintLease(CreateGraphics(dc), owned: true);
    }

    private Graphics Graphics => _graphics ??= CreateGraphics(_dc);

    public void Dispose()
    {
        _graphics?.Dispose();
        _graphics = null;
        _current = _previous;
        if (_timer is { } timer)
        {
            Trace.TraceInformation("Paint {0}: {1:0.000} ms", _name, timer.Elapsed.TotalMilliseconds);
        }
    }

    private static Graphics CreateGraphics(IntPtr dc)
    {
        var graphics = Graphics.FromHdc(dc);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Pixel i covers [i, i+1), so integer rectangles fill whole pixels and half-pixel strokes stay crisp.
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        return graphics;
    }
}

/// <summary>A Graphics borrowed for one shape; GDI+ output is flushed so later GDI text draws on top of it.</summary>
internal readonly struct PaintLease : IDisposable
{
    private readonly bool _owned;

    public PaintLease(Graphics graphics, bool owned)
    {
        Graphics = graphics;
        _owned = owned;
    }

    public Graphics Graphics { get; }

    public void Dispose()
    {
        Graphics.Flush(FlushIntention.Sync);
        if (_owned) Graphics.Dispose();
    }
}
