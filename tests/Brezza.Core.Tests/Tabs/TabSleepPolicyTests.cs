using Brezza.Core.Abstractions;
using Brezza.Core.Tabs;

namespace Brezza.Core.Tests.Tabs;

public sealed class TabSleepPolicyTests
{
    [Fact]
    public void An_idle_inactive_tab_may_be_released()
    {
        var outcome = TabSleepPolicy.Evaluate(
            new TabHostSignals(isLoading: false, isDocumentPlayingAudio: false, containsFullScreenElement: false),
            isActiveTab: false);

        Assert.Equal(TabSleepOutcome.Released, outcome);
    }

    [Fact]
    public void The_active_tab_is_never_released()
    {
        var outcome = TabSleepPolicy.Evaluate(
            new TabHostSignals(isLoading: false, isDocumentPlayingAudio: false, containsFullScreenElement: false),
            isActiveTab: true);

        Assert.Equal(TabSleepOutcome.ActiveTab, outcome);
    }

    [Theory]
    [InlineData(true, false, false, TabSleepOutcome.Loading)]
    [InlineData(false, true, false, TabSleepOutcome.PlayingAudio)]
    [InlineData(false, false, true, TabSleepOutcome.FullScreen)]
    public void Working_tabs_are_kept_alive(bool isLoading, bool isPlayingAudio, bool isFullScreen, TabSleepOutcome expected)
    {
        var outcome = TabSleepPolicy.Evaluate(
            new TabHostSignals(isLoading, isPlayingAudio, isFullScreen),
            isActiveTab: false);

        Assert.Equal(expected, outcome);
    }

    [Fact]
    public void A_host_that_cannot_be_evaluated_is_treated_as_busy()
    {
        var signals = TabHostSignals.FromHost(null!);

        Assert.True(signals.IsLoading);
        Assert.Equal(TabSleepOutcome.Loading, TabSleepPolicy.Evaluate(signals, isActiveTab: false));
    }

    [Fact]
    public void Host_signals_are_read_from_the_host()
    {
        var host = new FakeHost { IsLoading = true, IsDocumentPlayingAudio = true, ContainsFullScreenElement = true };

        var signals = TabHostSignals.FromHost(host);

        Assert.True(signals.IsLoading);
        Assert.True(signals.IsDocumentPlayingAudio);
        Assert.True(signals.ContainsFullScreenElement);
    }

    private sealed class FakeHost : ITabHost
    {
        public string Source => string.Empty;

        public string Title => string.Empty;

        public string FaviconUri => string.Empty;

        public Task<Stream?> GetFaviconAsync() => Task.FromResult<Stream?>(null);

        public bool TryPauseDownload(string targetFilePath) => false;

        public bool TryResumeDownload(string targetFilePath) => false;

        public bool TryCancelDownload(string targetFilePath) => false;

        public bool CanGoBack => false;

        public bool CanGoForward => false;

        public bool IsLoading { get; set; }

        public bool IsDocumentPlayingAudio { get; set; }

        public bool ContainsFullScreenElement { get; set; }

        public Task NavigateAsync(Uri uri) => Task.CompletedTask;

        public void GoBack() { }

        public void GoForward() { }

        public void Reload() { }

        // The interface requires the full event surface; this fake only reports host signals.
#pragma warning disable CS0067
        public event EventHandler? SourceChanged;

        public event EventHandler? TitleChanged;

        public event EventHandler? FaviconChanged;

        public event EventHandler? ProcessFailed;

        public event EventHandler? NavigationFailed;

        public event EventHandler<TabNavigationResultEventArgs>? NavigationCompleted;

        public event EventHandler<PopupRequestedEventArgs>? PopupRequested;

        public event EventHandler<DownloadStartedEventArgs>? DownloadStarted;

        public event EventHandler<DownloadStatusChangedEventArgs>? DownloadStatusChanged;

        public event EventHandler<DownloadProgressChangedEventArgs>? DownloadProgressChanged;

        public event EventHandler<PermissionRequestedEventArgs>? PermissionRequested;
#pragma warning restore CS0067

        public void SetVisible(bool visible) { }

        public void SetBounds(int x, int y, int width, int height) { }

        public void SetRasterizationScale(double scale) { }

        public void SetBackgroundColor(uint color) { }

        public void SetPrefersDarkColorScheme(bool dark) { }

        public void Focus() { }

        public void Dispose() { }
    }
}
