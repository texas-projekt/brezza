using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Brezza.App.Native;

/// <summary>
/// The system's folder picker, shown the way the shell shows its own windows: a modal dialog owned
/// by the window that asked for it. The settings panel picks a downloads folder with it instead of
/// asking the user to type a path. The browser never writes to the file system to show it, and a
/// dialog the system refuses to show is reported as a dismissal rather than thrown.
/// </summary>
internal static class NativeFolderPicker
{
    // HRESULT_FROM_WIN32(ERROR_CANCELLED): the user dismissed the dialog.
    private const int Cancelled = unchecked((int)0x800704C7);

    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint NoChangeDirectory = 0x8;
    private const uint PathMustExist = 0x800;
    private const uint DisplayNameFileSystemPath = 0x80058000;

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private sealed class FileOpenDialog
    {
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(IntPtr parent);

        // IFileDialog, in the exact vtable order. The Advise/Unadvise pair is declared even though
        // this picker never subscribes to events: omitting them would shift SetOptions and every
        // later method onto the wrong slot.
        void SetFileTypes(uint count, IntPtr types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close();
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint name, [MarshalAs(UnmanagedType.LPWStr)] out string value);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context, ref Guid riid, out IShellItem item);

    /// <summary>
    /// Shows the picker over <paramref name="owner"/> and returns the folder the user chose, or
    /// null when the dialog was dismissed or the system would not show it.
    /// </summary>
    /// <remarks>
    /// The picker is a convenience, so nothing it does is worth a browser crash: a corrupted-state
    /// exception from the COM surface is caught too, and a picker the system refuses is reported
    /// as a dismissal rather than thrown.
    /// </remarks>
    [HandleProcessCorruptedStateExceptions]
    public static string? TryPick(IntPtr owner, string? initialPath, string title)
    {
        IFileDialog? dialog = null;
        IShellItem? initial = null;
        IShellItem? result = null;
        try
        {
            // The picker is created through its coclass: the runtime builds the COM object behind
            // it, so the shell never calls CoCreateInstance itself.
            dialog = (IFileDialog)Activator.CreateInstance(typeof(FileOpenDialog))!;
            System.Diagnostics.Trace.TraceInformation("Folder picker: dialog created.");
            dialog.SetOptions(PickFolders | ForceFileSystem | NoChangeDirectory | PathMustExist);
            System.Diagnostics.Trace.TraceInformation("Folder picker: options set.");
            if (!string.IsNullOrWhiteSpace(initialPath) && TryCreateShellItem(initialPath!, out initial))
            {
                dialog.SetFolder(initial);
                System.Diagnostics.Trace.TraceInformation("Folder picker: initial folder set.");
            }

            if (title.Length > 0)
            {
                dialog.SetTitle(title);
                System.Diagnostics.Trace.TraceInformation("Folder picker: title set.");
            }

            var shown = dialog.Show(owner);
            System.Diagnostics.Trace.TraceInformation("Folder picker: Show returned 0x{0:X8}.", shown);
            if (shown != 0 && shown != Cancelled) return null;
            dialog.GetResult(out result);
            System.Diagnostics.Trace.TraceInformation("Folder picker: GetResult returned {0}.", result is null ? "null" : "an item");
            if (result is null) return null;

            result.GetDisplayName(DisplayNameFileSystemPath, out var folder);
            System.Diagnostics.Trace.TraceInformation("Folder picker: display name read.");
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidCastException or NotSupportedException or
                ArgumentException or AccessViolationException or SEHException)
        {
            // A picker the system refuses is a dismissal: the caller keeps its own error path.
            System.Diagnostics.Trace.TraceWarning("Folder picker: {0}", exception);
            return null;
        }
        finally
        {
            Release(ref result);
            Release(ref initial);
            Release(ref dialog);
        }
    }

    private static bool TryCreateShellItem(string path, out IShellItem item)
    {
        var riid = typeof(IShellItem).GUID;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref riid, out item);
            return item is not null;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or NotSupportedException)
        {
            item = null!;
            return false;
        }
    }

    private static void Release<T>(ref T? comObject) where T : class
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception exception) when (exception is COMException or ArgumentException)
        {
            // A reference the runtime already released leaves nothing to clean up.
        }

        comObject = null;
    }
}
