using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Script.Serialization;
using Brezza.Core.Bridge;
using Brezza.WebView2;

namespace Brezza.App;

internal sealed class BrowserChromeBridge
{
    private readonly WebView2ChromeHost _host;
    private readonly JavaScriptSerializer _serializer = new();

    public BrowserChromeBridge(WebView2ChromeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _host.MessageReceived += OnMessageReceived;
    }

    public event EventHandler<BrowserUiCommand>? CommandReceived;

    public void Send(object message)
    {
        if (!_host.IsReady) return;
        _host.PostMessage(_serializer.Serialize(message));
    }

    private void OnMessageReceived(object? sender, ChromeMessageEventArgs e)
        => AcceptMessageJson(e.Json, fromInternalPage: false);

    public void AcceptMessageJson(string json, bool fromInternalPage)
    {
        try
        {
            var envelope = _serializer.Deserialize<Dictionary<string, object>>(json);
            if (!BrowserUiCommandParser.TryParse(envelope, out var command, out var error))
            {
                Trace.TraceWarning("Rejected browser chrome message: {0}", error);
                return;
            }

            if (fromInternalPage && BrowserUiCommandParser.IsChromeSurfaceOnly(command!.Type))
            {
                Trace.TraceWarning("Rejected chrome-only command from an internal page: {0}", command.Type);
                return;
            }

            CommandReceived?.Invoke(this, command!);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Could not parse browser chrome message: {0}", exception.Message);
        }
    }
}
