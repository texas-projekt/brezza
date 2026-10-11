using System;
using System.Collections.Generic;
using System.Globalization;

namespace Brezza.Core.Bridge;

public static class BrowserUiCommandParser
{
    public const int CurrentProtocolVersion = 1;

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "ready", "navigate", "newTab", "newPrivateTab", "activateTab", "closeTab",
        "back", "forward", "reload", "toggleBookmark", "saveSettings",
        "openSettings", "openHistory", "openBookmarks", "internalReady", "closeInternalPage", "focusAddress",
        "layout", "removeBookmark",
    };

    private static readonly HashSet<string> ChromeSurfaceOnlyTypes = new(StringComparer.Ordinal)
    {
        "layout",
    };

    public const int MaximumChromeHeight = 1000;

    /// <summary>Commands that only the chrome surface may send; internal pages in a tab must not.</summary>
    public static bool IsChromeSurfaceOnly(string type) => ChromeSurfaceOnlyTypes.Contains(type);

    public static bool TryParse(
        IDictionary<string, object>? envelope,
        out BrowserUiCommand? command,
        out string error)
    {
        command = null;
        error = "Invalid message.";
        if (envelope is null || !TryGetVersion(envelope, out var version) || version != CurrentProtocolVersion ||
            !envelope.TryGetValue("type", out var typeValue) || typeValue is not string type || !AllowedTypes.Contains(type))
        {
            return false;
        }

        var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
        if (envelope.TryGetValue("args", out var argsValue))
        {
            if (argsValue is not IDictionary<string, object> suppliedArguments) return false;
            foreach (var pair in suppliedArguments) arguments[pair.Key] = pair.Value;
        }

        if (!HasValidArguments(type, arguments))
        {
            error = "Invalid command arguments.";
            return false;
        }

        command = new BrowserUiCommand(type, arguments);
        error = string.Empty;
        return true;
    }

    private static bool TryGetVersion(IDictionary<string, object> envelope, out int version)
    {
        version = 0;
        if (!envelope.TryGetValue("version", out var value) || value is null) return false;
        try
        {
            version = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException)
        {
            return false;
        }
    }

    private static bool HasValidArguments(string type, IDictionary<string, object> arguments)
    {
        switch (type)
        {
            case "navigate":
                return HasString(arguments, "text", 8192, allowEmpty: false);
            case "activateTab":
            case "closeTab":
                return HasString(arguments, "tabId", 128, allowEmpty: false);
            case "saveSettings":
                return HasString(arguments, "searchTemplate", 2048, allowEmpty: false);
            case "internalReady":
                return HasString(arguments, "page", 32, allowEmpty: false);
            case "removeBookmark":
                return HasString(arguments, "url", 8192, allowEmpty: false);
            case "layout":
                return HasInt32(arguments, "height", 0, MaximumChromeHeight);
            default:
                return true;
        }
    }

    private static bool HasString(IDictionary<string, object> arguments, string name, int maximumLength, bool allowEmpty)
    {
        return arguments.TryGetValue(name, out var value) && value is string text &&
               text.Length <= maximumLength && (allowEmpty || !string.IsNullOrWhiteSpace(text));
    }

    private static bool HasInt32(IDictionary<string, object> arguments, string name, int minimum, int maximum)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null) return false;
        try
        {
            var number = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return number >= minimum && number <= maximum;
        }
        catch (Exception exception) when (exception is FormatException || exception is InvalidCastException || exception is OverflowException)
        {
            return false;
        }
    }
}
