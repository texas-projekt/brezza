using System;
using System.Collections.Generic;

namespace Brezza.Core.Navigation;

/// <summary>
/// Limits page-initiated popups. A popup is allowed only with a user gesture, at most one per tab
/// in a short window, and at most five per minute across the browser.
/// </summary>
public sealed class PopupRequestLimiter
{
    private const int MaximumPerMinute = 5;
    private static readonly TimeSpan PerTabCooldown = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, DateTime> _lastByTab = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _recent = new();
    private readonly object _gate = new();

    public bool TryAcquire(string? tabId, bool userInitiated, DateTime now)
    {
        if (!userInitiated) return false;
        var key = string.IsNullOrEmpty(tabId) ? "tab:unknown" : tabId!;

        lock (_gate)
        {
            while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(1)) _recent.Dequeue();
            if (_recent.Count >= MaximumPerMinute) return false;
            if (_lastByTab.TryGetValue(key, out var last) && now - last < PerTabCooldown) return false;

            _lastByTab[key] = now;
            _recent.Enqueue(now);
            return true;
        }
    }
}
