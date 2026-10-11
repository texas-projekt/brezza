using System;

namespace Brezza.Core.Abstractions;

public sealed class ChromeMessageEventArgs : EventArgs
{
    public ChromeMessageEventArgs(string json) => Json = json;

    public string Json { get; }
}
