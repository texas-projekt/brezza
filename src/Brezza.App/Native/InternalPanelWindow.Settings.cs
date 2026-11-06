using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Brezza.App.Native;
using Brezza.Core.Abstractions;
using Brezza.Core.Downloads;
using Brezza.Core.Navigation;
using Brezza.Core.Privacy;
using Brezza.Persistence;

namespace Brezza.App;

/// <summary>The settings form: its lists, dirty tracking, and the submission it raises.</summary>
internal sealed partial class InternalPanelWindow
{
    // ---------- Settings ----------

    private void RebuildPresetList()
    {
        var items = SearchProviderPresets.All.Select(preset => preset.Name).Concat(new[] { _localization.Get("settings.custom") });
        var selected = (int)NativeMethods.SendMessage(_presetCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        FillCombo(_presetCombo, items, selected);
    }

    // Relabeling a combo keeps the user's current choice, so re-applying the language after a save
    // cannot silently revert a selection that was just saved.
    private void RebuildLanguageList()
    {
        FillCombo(_languageCombo, new[]
        {
            _localization.Get("settings.languageSystem"),
            _localization.Get("settings.languageEnglish"),
            _localization.Get("settings.languageChinese"),
            _localization.Get("settings.languageTraditional"),
        }, CurrentSelection(_languageCombo, LanguageIndex(_savedLanguage)));
    }

    private void RebuildThemeList()
    {
        FillCombo(_themeCombo, new[]
        {
            _localization.Get("settings.themeSystem"),
            _localization.Get("settings.themeLight"),
            _localization.Get("settings.themeDark"),
        }, CurrentSelection(_themeCombo, ThemeIndex(_savedTheme)));
    }

    private void RebuildStartupList()
    {
        FillCombo(_startupCombo, new[]
        {
            _localization.Get("settings.startupNewTab"),
            _localization.Get("settings.startupRestore"),
        }, CurrentSelection(_startupCombo, StartupIndex(_savedStartup)));
    }

    private static int CurrentSelection(IntPtr combo, int fallback)
    {
        var selected = (int)NativeMethods.SendMessage(combo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return selected >= 0 ? selected : fallback;
    }

    private static void FillCombo(IntPtr combo, IEnumerable<string> items, int selected)
    {
        if (combo == IntPtr.Zero) return;
        NativeMethods.SendMessage(combo, NativeMethods.CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        foreach (var item in items) NativeMethods.SendMessage(combo, NativeMethods.CB_ADDSTRING, IntPtr.Zero, item);
        NativeMethods.SendMessage(combo, NativeMethods.CB_SETCURSEL, new IntPtr(selected), IntPtr.Zero);
    }

    private void SelectLanguage(string language) =>
        NativeMethods.SendMessage(_languageCombo, NativeMethods.CB_SETCURSEL, new IntPtr(LanguageIndex(language)), IntPtr.Zero);

    private void SelectTheme(BrowserThemeMode theme) =>
        NativeMethods.SendMessage(_themeCombo, NativeMethods.CB_SETCURSEL, new IntPtr(ThemeIndex(theme)), IntPtr.Zero);

    private void SelectStartup(BrowserStartupBehavior behavior) =>
        NativeMethods.SendMessage(_startupCombo, NativeMethods.CB_SETCURSEL, new IntPtr(StartupIndex(behavior)), IntPtr.Zero);

    private void SelectPreset(string template)
    {
        var presets = SearchProviderPresets.All;
        var index = presets.Select((preset, i) => (preset, i))
            .FirstOrDefault(pair => string.Equals(pair.preset.Template, template, StringComparison.OrdinalIgnoreCase));
        var presetIndex = index.preset is null ? presets.Count : index.i;
        NativeMethods.SendMessage(_presetCombo, NativeMethods.CB_SETCURSEL, new IntPtr(presetIndex), IntPtr.Zero);
    }

    private static int ThemeIndex(BrowserThemeMode theme) => theme switch
    {
        BrowserThemeMode.Light => 1,
        BrowserThemeMode.Dark => 2,
        _ => 0,
    };

    /// <summary>Index of a startup value in the settings combo: new tab page, last session.</summary>
    private static int StartupIndex(BrowserStartupBehavior behavior) =>
        behavior == BrowserStartupBehavior.RestoreSession ? 1 : 0;

    /// <summary>Index of a language value in the settings combo: system, English, Simplified, Traditional.</summary>
    private static int LanguageIndex(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return 0;
        var normalized = Localization.Normalize(language);
        // Normalize maps unknown values to English; an unknown stored value is better shown as
        // the system default than as an explicit English choice.
        if (normalized == Localization.English &&
            !language!.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase)) return 0;
        return normalized switch
        {
            Localization.TraditionalChinese => 3,
            Localization.Chinese => 2,
            Localization.English => 1,
            _ => 0,
        };
    }

    private void ApplyPresetSelection()
    {
        var index = (int)NativeMethods.SendMessage(_presetCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        var presets = SearchProviderPresets.All;
        if (index < 0 || index >= presets.Count)
        {
            // "Custom" keeps whatever the user types.
            SetStatus("settings.customHint");
            return;
        }

        NativeMethods.SetWindowText(_templateEdit, presets[index].Template);
        SetStatus(null);
    }

    private void SaveSettings()
    {
        var template = NativeMethods.GetWindowText(_templateEdit).Trim();
        SaveSettingsRequested?.Invoke(this, new SettingsSubmission(
            template, LanguageValue(), ThemeValue(), StartupValue(), DownloadFolderValue()));
    }

    /// <summary>The folder typed or picked in the form, trimmed; empty means the default folder.</summary>
    private string DownloadFolderValue() => NativeMethods.GetWindowText(_downloadFolderEdit).Trim();

    /// <summary>Cancel works like a dialog's Cancel: unsaved changes are dropped and the panel closes.</summary>
    private void CancelAndClose()
    {
        NativeMethods.SetWindowText(_templateEdit, _savedTemplate);
        NativeMethods.SetWindowText(_downloadFolderEdit, _savedDownloadFolder);
        SelectLanguage(_savedLanguage);
        SelectTheme(_savedTheme);
        SelectStartup(_savedStartup);
        SelectPreset(_savedTemplate);
        SetStatus(null);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Save is available only when something differs from the saved settings.</summary>
    private void UpdateDirty()
    {
        if (_saveButton == IntPtr.Zero) return;
        var dirty =
            !string.Equals(NativeMethods.GetWindowText(_templateEdit).Trim(), _savedTemplate, StringComparison.Ordinal) ||
            !string.Equals(DownloadFolderValue(), _savedDownloadFolder, StringComparison.Ordinal) ||
            !string.Equals(LanguageValue(), _savedLanguage, StringComparison.Ordinal) ||
            ThemeValue() != _savedTheme ||
            StartupValue() != _savedStartup;
        NativeMethods.EnableWindow(_saveButton, dirty);
        // A pending change replaces the last save result, which no longer describes the form.
        if (dirty && _statusKey == "settings.saved") SetStatus(null);
    }

    private string LanguageValue()
    {
        var index = (int)NativeMethods.SendMessage(_languageCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return index switch
        {
            3 => Localization.TraditionalChinese,
            2 => Localization.Chinese,
            1 => Localization.English,
            _ => string.Empty,
        };
    }

    private BrowserThemeMode ThemeValue()
    {
        var index = (int)NativeMethods.SendMessage(_themeCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return index switch
        {
            1 => BrowserThemeMode.Light,
            2 => BrowserThemeMode.Dark,
            _ => BrowserThemeMode.System,
        };
    }

    private BrowserStartupBehavior StartupValue()
    {
        var index = (int)NativeMethods.SendMessage(_startupCombo, NativeMethods.CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return index == 1 ? BrowserStartupBehavior.RestoreSession : BrowserStartupBehavior.NewTab;
    }

    private void SetStatus(string? key)
    {
        _statusKey = key;
        NativeMethods.SetWindowText(_statusText, key is null ? string.Empty : _localization.Get(key));
    }

    /// <summary>Shows status text that is not a localized key, such as a failure with its detail.</summary>
    private void SetStatusText(string? text)
    {
        _statusKey = null;
        NativeMethods.SetWindowText(_statusText, text ?? string.Empty);
    }
}
