using System;

namespace Brezza.WebView2;

public sealed class ChromeMessageEventArgs : EventArgs
{
    public ChromeMessageEventArgs(string json)
    {
        Json = json;
    }

    public string Json { get; }
}
