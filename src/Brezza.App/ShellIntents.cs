using System;
using System.Collections.Generic;

namespace Brezza.App;

/// <summary>What a tab's context menu asks for.</summary>
internal enum TabCommandKind
{
    Reload,
    Duplicate,
    Close,
    CloseOthers,
    CloseToTheRight,
}

/// <summary>The action a notice offers next to Dismiss.</summary>
internal enum NoticeAction
{
    None,

    /// <summary>Reload a page whose process stopped.</summary>
    Reload,

    /// <summary>Try the address that failed again.</summary>
    Retry,
}

/// <summary>A context-menu command and the tab it was opened on.</summary>
internal readonly struct TabCommand
{
    public TabCommand(string tabId, TabCommandKind kind)
    {
        TabId = tabId ?? throw new ArgumentNullException(nameof(tabId));
        Kind = kind;
    }

    public string TabId { get; }

    public TabCommandKind Kind { get; }
}

/// <summary>One shortcut on the new-tab page.</summary>
internal sealed class StartPageEntry
{
    public StartPageEntry(string title, string url)
    {
        Url = url ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(title) ? Url : title.Trim();
    }

    public string Title { get; }

    public string Url { get; }
}

/// <summary>The shortcuts the new-tab page lists, grouped by where they come from.</summary>
internal sealed class StartPageEntries
{
    public static readonly StartPageEntries Empty = new(Array.Empty<StartPageEntry>(), Array.Empty<StartPageEntry>());

    public StartPageEntries(IReadOnlyList<StartPageEntry> recent, IReadOnlyList<StartPageEntry> bookmarks)
    {
        Recent = recent;
        Bookmarks = bookmarks;
    }

    public IReadOnlyList<StartPageEntry> Recent { get; }

    public IReadOnlyList<StartPageEntry> Bookmarks { get; }

    public bool SameAs(StartPageEntries? other) =>
        other is not null && Same(Recent, other.Recent) && Same(Bookmarks, other.Bookmarks);

    private static bool Same(IReadOnlyList<StartPageEntry> left, IReadOnlyList<StartPageEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Url, right[index].Url, StringComparison.Ordinal) ||
                !string.Equals(left[index].Title, right[index].Title, StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
