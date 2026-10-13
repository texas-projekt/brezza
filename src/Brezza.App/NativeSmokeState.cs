using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Brezza.Core.Tabs;

#if DEBUG
namespace Brezza.App;
/// <summary>
/// Debug-only observable state used by the Windows smoke harness. It is written only when
/// <c>BREZZA_SMOKE_STATE</c> is set and only in Debug builds, so release output carries no extra
/// behavior. The file lets an external test assert the real native controls without a script bridge.
/// </summary>
internal static class NativeSmokeState
{
    public static void Write(
        MainWindow window,
        TabRegistry? tabs,
        Core.Tabs.TabRuntimeManager? runtime,
        string? panelMode)
    {
        var path = Environment.GetEnvironmentVariable("BREZZA_SMOKE_STATE");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var snapshot = new StringBuilder();
            snapshot.Append('{');
            AppendString(snapshot, "panelMode", panelMode ?? string.Empty, first: true);
            AppendString(snapshot, "windowTitle", window.WindowTitle);
            AppendNumber(snapshot, "tabCount", window.TabCount);
            AppendNumber(snapshot, "selectedTab", window.SelectedTabIndex);
            AppendNumber(snapshot, "noticeVisible", window.IsNoticeVisible ? 1 : 0);
            AppendString(snapshot, "noticeText", window.NoticeText);
            AppendString(snapshot, "addressText", window.AddressText);
            AppendNumber(snapshot, "newTabPageVisible", window.IsNewTabPageVisible ? 1 : 0);
            AppendNumber(snapshot, "backEnabled", window.IsControlEnabled(MainWindow.IdBack) ? 1 : 0);
            AppendNumber(snapshot, "forwardEnabled", window.IsControlEnabled(MainWindow.IdForward) ? 1 : 0);
            AppendNumber(snapshot, "menuButtonVisible", window.IsControlVisible(MainWindow.IdMenu) ? 1 : 0);
            AppendString(snapshot, "activeTabId", tabs?.ActiveTab?.Id ?? string.Empty);
            AppendString(snapshot, "activeTabUrl", tabs?.ActiveTab?.Url ?? string.Empty);
            AppendString(snapshot, "activeHostSource", runtime?.ActiveHost?.Source ?? string.Empty);
            AppendString(snapshot, "activeHostTitle", runtime?.ActiveHost?.Title ?? string.Empty);
            AppendString(snapshot, "tabTitles", string.Join("\u001f", tabs?.Tabs.Select(tab => tab.Id + "|" + tab.Url + "|" + tab.Title + "|" + (tab.SessionKind == BrowserSessionKind.Private ? "1" : "0")) ?? Enumerable.Empty<string>()));
            AppendString(snapshot, "nativeTabTexts", string.Join("\u001f", Enumerable.Range(0, window.TabCount).Select(index => window.GetTabText(index))));
            snapshot.Append('}');

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, snapshot.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            // The state file is a test aid; a failed write must never disturb the browser.
        }
    }

    private static void AppendString(StringBuilder builder, string name, string value, bool first = false)
    {
        if (!first) builder.Append(',');
        builder.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');
    }

    private static void AppendNumber(StringBuilder builder, string name, int value, bool first = false)
    {
        if (!first) builder.Append(',');
        builder.Append('"').Append(name).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", string.Empty);
}
#endif
