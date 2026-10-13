param(
    [int]$ProcessId = 0,
    [long]$WindowHandle = 0,
    [int]$Width = 0,
    [int]$Height = 0,
    [int]$FocusControlId = 0,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$targetWindow = [IntPtr]::new($WindowHandle)
if ($targetWindow -eq [IntPtr]::Zero) {
    if ($ProcessId -le 0) { throw 'Specify either ProcessId or WindowHandle.' }
    $process = Get-Process -Id $ProcessId
    $targetWindow = $process.MainWindowHandle
    if ($targetWindow -eq [IntPtr]::Zero) { throw "Process $ProcessId has no top-level window." }
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;

public static class BrezzaWindowCapture
{
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hWnd, int controlId);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hDc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDc, IntPtr objectHandle);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr objectHandle);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hDc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER
    {
        public uint size; public int width, height; public ushort planes, bitsPerPixel;
        public uint compression, imageSize, xPixelsPerMeter, yPixelsPerMeter, colorsUsed, importantColors;
    }

    public static void Save(IntPtr window, string path)
    {
        RECT rect;
        if (!GetWindowRect(window, out rect)) throw new InvalidOperationException("GetWindowRect failed.");
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;
        var screenDc = GetWindowDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        if (!PrintWindow(window, memoryDc, 0)) throw new InvalidOperationException("PrintWindow failed.");

        var info = new BITMAPINFOHEADER
        {
            size = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)), width = width, height = height,
            planes = 1, bitsPerPixel = 32
        };
        var pixels = new byte[width * height * 4];
        GetDIBits(screenDc, bitmap, 0, (uint)height, pixels, ref info, 0);
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using (var stream = File.Create(path))
        {
            var offset = 14 + Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            stream.WriteByte(0x42); stream.WriteByte(0x4D);
            WriteUInt32(stream, (uint)(offset + pixels.Length));
            WriteUInt16(stream, 0); WriteUInt16(stream, 0); WriteUInt32(stream, (uint)offset);
            WriteStruct(stream, info);
            stream.Write(pixels, 0, pixels.Length);
        }

        SelectObject(memoryDc, previous); DeleteObject(bitmap); DeleteDC(memoryDc); ReleaseDC(IntPtr.Zero, screenDc);
    }

    static void WriteUInt16(Stream stream, int value) { stream.WriteByte((byte)value); stream.WriteByte((byte)(value >> 8)); }
    static void WriteUInt32(Stream stream, uint value) { for (var i = 0; i < 4; i++) stream.WriteByte((byte)(value >> (8 * i))); }
    static void WriteStruct(Stream stream, BITMAPINFOHEADER value)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BITMAPINFOHEADER)));
        Marshal.StructureToPtr(value, pointer, false);
        var bytes = new byte[Marshal.SizeOf(typeof(BITMAPINFOHEADER))];
        Marshal.Copy(pointer, bytes, 0, bytes.Length); Marshal.FreeHGlobal(pointer);
        stream.Write(bytes, 0, bytes.Length);
    }
}
'@

if ($Width -gt 0 -and $Height -gt 0) {
    [BrezzaWindowCapture]::SetWindowPos($targetWindow, [IntPtr]::Zero, 0, 0, $Width, $Height, 0x0004) | Out-Null
    Start-Sleep -Milliseconds 150
}
if ($FocusControlId -gt 0) {
    $focus = [BrezzaWindowCapture]::GetDlgItem($targetWindow, $FocusControlId)
    if ($focus -ne [IntPtr]::Zero) {
        [BrezzaWindowCapture]::SetFocus($focus) | Out-Null
        Start-Sleep -Milliseconds 100
    }
}
[BrezzaWindowCapture]::Save($targetWindow, $OutputPath)
Write-Output "Saved $OutputPath"
