using System;
using Microsoft.Web.WebView2.Core;

namespace Brezza.WebView2;

/// <summary>Raised when the Evergreen WebView2 Runtime is not installed.</summary>
public sealed class WebView2RuntimeNotFoundException : Exception
{
    public WebView2RuntimeNotFoundException()
        : base("Microsoft Edge WebView2 Runtime is required.")
    {
    }
}

/// <summary>
/// Reports whether the Evergreen WebView2 Runtime is installed. The application asks the adapter
/// instead of calling WebView2 COM APIs itself.
/// </summary>
public static class WebView2Runtime
{
    public static bool IsAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception exception) when (exception is InvalidOperationException || exception is System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public static void EnsureAvailable()
    {
        if (!IsAvailable()) throw new WebView2RuntimeNotFoundException();
    }
}
