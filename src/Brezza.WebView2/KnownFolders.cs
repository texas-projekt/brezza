using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Brezza.WebView2;

/// <summary>
/// Resolves the user's Downloads folder through the Windows Known Folder API, so a redirected or
/// OneDrive folder is honoured instead of assuming %USERPROFILE%\Downloads.
/// </summary>
internal static class KnownFolders
{
    // FOLDERID_Downloads
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads()
    {
        IntPtr path = IntPtr.Zero;
        try
        {
            var folderId = DownloadsFolderId;
            SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out path);
            var value = Marshal.PtrToStringUni(path);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        catch (Exception exception) when (exception is COMException || exception is ArgumentException)
        {
            Trace.TraceWarning("The Downloads Known Folder could not be resolved: {0}", exception.Message);
        }
        finally
        {
            if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
