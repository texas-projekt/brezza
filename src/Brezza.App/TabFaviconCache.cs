using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Brezza.Core.Tabs;

namespace Brezza.App;

/// <summary>
/// A rendering cache that turns each tab's Core-tracked favicon URI into a bitmap the tab strip
/// paints. Core owns which favicon a tab has; this cache holds only the decoded image, keyed by
/// tab, so an icon survives its tab's host sleeping or being released and does not blink out while
/// a background tab has no surface. Images are fetched through the tab's own host, never by a
/// second network stack of the shell's own.
/// </summary>
internal sealed class TabFaviconCache : IDisposable
{
    private sealed class Icon
    {
        public Icon(string uri, Bitmap image)
        {
            Uri = uri;
            Image = image;
        }

        /// <summary>The URI the image was fetched for, so a stale icon is recognized on a sync.</summary>
        public string Uri { get; }

        public Bitmap Image { get; }
    }

    private readonly Dictionary<string, Icon> _icons = new(StringComparer.Ordinal);

    // The favicon URI each tab currently has a fetch in flight for. A fetch that a newer URI
    // replaced while it was running is dropped when it lands, so the icon always matches the URI
    // Core tracks for the tab.
    private readonly Dictionary<string, string> _loading = new(StringComparer.Ordinal);
    // A document that genuinely has no favicon, keyed by the URI it was fetched for. Without this,
    // every state render asks the host again for the same empty result.
    private readonly Dictionary<string, string> _missing = new(StringComparer.Ordinal);
    private readonly Func<string, Task<Stream?>> _fetch;
    private readonly Func<string, bool> _hasContent;
    private readonly Action _repaint;
    private bool _disposed;

    public TabFaviconCache(Func<string, Task<Stream?>> fetch, Func<string, bool> hasContent, Action repaint)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _hasContent = hasContent ?? throw new ArgumentNullException(nameof(hasContent));
        _repaint = repaint ?? throw new ArgumentNullException(nameof(repaint));
    }

    /// <summary>The bitmap for a tab, or null when the tab has no favicon image.</summary>
    public Bitmap? Image(string tabId) => _icons.TryGetValue(tabId, out var icon) ? icon.Image : null;

    /// <summary>
    /// Aligns the cache with the current tabs: fetches the image of every tab whose favicon URI is
    /// new, clears the icon of a tab whose document has none, and drops closed tabs. Called on
    /// every state render, so a tab whose host did not exist yet (new, internal, or sleeping) is
    /// picked up as soon as one does.
    /// </summary>
    public void Sync(IReadOnlyList<BrowserTab> tabs)
    {
        if (_disposed) return;

        var live = new HashSet<string>(StringComparer.Ordinal);
        var cleared = false;
        foreach (var tab in tabs)
        {
            live.Add(tab.Id);
            if (string.IsNullOrEmpty(tab.FaviconUri))
            {
                // A document without a favicon shows no icon, and the previous page's is gone.
                if (_icons.Remove(tab.Id)) cleared = true;
                _missing.Remove(tab.Id);
                continue;
            }

            if (_loading.TryGetValue(tab.Id, out var inFlight) &&
                string.Equals(inFlight, tab.FaviconUri, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_icons.TryGetValue(tab.Id, out var icon) &&
                string.Equals(icon.Uri, tab.FaviconUri, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_missing.TryGetValue(tab.Id, out var missingUri) &&
                string.Equals(missingUri, tab.FaviconUri, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A tab without a controller may simply not have loaded yet. Do not record that as
            // "no favicon"; only a host that can answer may produce a negative result.
            if (!_hasContent(tab.Id)) continue;

            // A URI whose image is not cached yet is fetched again on the next sync: the tab may
            // simply have had no host (new, internal, or sleeping), and one can appear later.
            _loading[tab.Id] = tab.FaviconUri;
            _ = LoadAsync(tab.Id, tab.FaviconUri);
        }

        foreach (var closed in _icons.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            if (_icons.TryGetValue(closed, out var dropped) && _icons.Remove(closed))
            {
                dropped.Image.Dispose();
            }

            _missing.Remove(closed);
        }

        foreach (var closed in _missing.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _missing.Remove(closed);
        }

        if (cleared) _repaint();
    }

    private async Task LoadAsync(string tabId, string uri)
    {
        try
        {
            var stream = await _fetch(tabId).ConfigureAwait(true);
            if (_disposed)
            {
                stream?.Dispose();
                return;
            }

            Bitmap? image = null;
            if (stream is not null)
            {
                try
                {
                    image = new Bitmap(stream);
                }
                finally
                {
                    stream.Dispose();
                }
            }

            // A fetch that a newer URI replaced while it was in flight is dropped.
            if (_loading.TryGetValue(tabId, out var current) &&
                !string.Equals(current, uri, StringComparison.OrdinalIgnoreCase))
            {
                image?.Dispose();
                return;
            }

            // No image yet: remember that this document has no favicon, so ordinary state renders
            // do not ask the host again. A different URI clears the negative entry in Sync.
            if (image is null)
            {
                if (_loading.TryGetValue(tabId, out current) &&
                    string.Equals(current, uri, StringComparison.OrdinalIgnoreCase))
                {
                    _missing[tabId] = uri;
                }

                return;
            }

            _missing.Remove(tabId);
            if (_icons.TryGetValue(tabId, out var previous) && _icons.Remove(tabId))
            {
                previous.Image.Dispose();
            }

            _icons[tabId] = new Icon(uri, image);
            _repaint();
        }
        catch (Exception exception)
        {
            // A favicon is decoration: a failed fetch is traced and the tab simply has no icon.
            Trace.TraceWarning("The favicon of {0} could not be loaded: {1}", tabId, exception.Message);
        }
        finally
        {
            if (_loading.TryGetValue(tabId, out var current) &&
                string.Equals(current, uri, StringComparison.OrdinalIgnoreCase))
            {
                _loading.Remove(tabId);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var icon in _icons.Values) icon.Image.Dispose();
        _icons.Clear();
        _missing.Clear();
    }
}
