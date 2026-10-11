using System;

namespace Brezza.Core.Navigation;

public enum NavigationTargetKind
{
    Empty,
    Url,
    Search,
}

public sealed class NavigationTarget
{
    private NavigationTarget(NavigationTargetKind kind, string input, Uri? uri)
    {
        Kind = kind;
        Input = input;
        Uri = uri;
    }

    public NavigationTargetKind Kind { get; }

    public string Input { get; }

    public Uri? Uri { get; }

    public static NavigationTarget Empty() => new(NavigationTargetKind.Empty, string.Empty, null);

    public static NavigationTarget Url(string input, Uri uri) => new(NavigationTargetKind.Url, input, uri);

    public static NavigationTarget Search(string input, Uri uri) => new(NavigationTargetKind.Search, input, uri);
}
