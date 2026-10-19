using System;
using System.Collections.Generic;

namespace Brezza.Core.Permissions;

/// <summary>
/// Keeps a page that repeatedly asks for a permission from flooding the notice area. One notice per
/// source and permission kind is shown in a 30-second window.
/// </summary>
public sealed class PermissionNoticeLimiter
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool ShouldNotify(string? sourceUrl, BrowserPermissionKind kind, DateTime now)
    {
        var key = (sourceUrl ?? string.Empty) + "|" + kind;
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var last) && now - last < Cooldown) return false;
            _last[key] = now;
            return true;
        }
    }
}
