using System.Collections.Generic;

namespace Brezza.Core.Bridge;

public sealed class BrowserUiCommand : System.EventArgs
{
    internal BrowserUiCommand(string type, IDictionary<string, object> arguments)
    {
        Type = type;
        Arguments = arguments;
    }

    public string Type { get; }

    public IDictionary<string, object> Arguments { get; }

    public string GetString(string name) => Arguments.TryGetValue(name, out var value) ? value as string ?? string.Empty : string.Empty;

    public int GetInt32(string name) => Arguments.TryGetValue(name, out var value) ? System.Convert.ToInt32(value) : 0;

    public bool GetBoolean(string name) => Arguments.TryGetValue(name, out var value) && value is true;
}
