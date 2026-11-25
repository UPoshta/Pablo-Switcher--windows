using Pablo_Switcher.Models;
using Pablo_Switcher.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Markup;

namespace Pablo_Switcher.Views;

public partial class SettingsWindow : Window
{
    private static readonly IReadOnlyList<HotkeyPreset> PresetOrder =
    [
        HotkeyPreset.DoubleShift,
        HotkeyPreset.DoubleAlt,
        HotkeyPreset.DoubleCtrl,
        HotkeyPreset.DoubleWin,
        HotkeyPreset.KeyF12,
        HotkeyPreset.KeyF6,
        HotkeyPreset.KeyCapsLock
    ];

    private static readonly IReadOnlyList<LangCode> LangOrder =
    [
        LangCode.Uk,
        LangCode.En
    ];

    private readonly XmlI18nManager _i18n;
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onSaveCallback;
    private bool _isClosingApp;

    public SettingsWindow(
        XmlI18nManager i18n,
        AppSettings settings,
        Action<AppSettings> onSaveCallback)
    {
        InitializeComponent();

        _i18n = i18n;
        _settings = settings;
        _onSaveCallback = onSaveCallback;
    }

    public void ShowAndActivate()
    {
        RefreshLocalizedTexts();
        SyncControlsFromSettings();

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    public void ForceClose()
    {
        _isClosingApp = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Аналог window.setReleasedWhenClosed_(False) — ховаємо вікно замість знищення
        if (!_isClosingApp)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void RefreshLocalizedTexts()
    {
        Title = _i18n.T("settings_title");
        LblLanguage.Text = _i18n.T("settings_ui_lang");
        LblHotkey.Text = _i18n.T("settings_hotkey");
        ChkAutoSwitch.Content = _i18n.T("settings_autoswitch_checkbox");
        LblHint.Text = _i18n.T("settings_hint");
        BtnSave.Content = _i18n.T("settings_save");

        ComboLanguage.Items.Clear();
        ComboLanguage.Items.Add("Українська (UA)");
        ComboLanguage.Items.Add("English (EN)");

        ComboHotkey.Items.Clear();
        foreach (HotkeyPreset preset in PresetOrder)
        {
            ComboHotkey.Items.Add(_i18n.HotkeyLabel(preset));
        }
    }

    private void SyncControlsFromSettings()
    {
        int langIdx = IndexOf(LangOrder, _settings.UiLanguage);
        ComboLanguage.SelectedIndex = langIdx >= 0 ? langIdx : 0;

        int hotkeyIdx = IndexOf(PresetOrder, _settings.HotkeyPreset);
        ComboHotkey.SelectedIndex = hotkeyIdx >= 0 ? hotkeyIdx : 0;

        ChkAutoSwitch.IsChecked = _settings.AutoSwitchEnabled;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (ComboLanguage.SelectedIndex >= 0 && ComboLanguage.SelectedIndex < LangOrder.Count)
        {
            _settings.UiLanguage = LangOrder[ComboLanguage.SelectedIndex];
        }

        if (ComboHotkey.SelectedIndex >= 0 && ComboHotkey.SelectedIndex < PresetOrder.Count)
        {
            _settings.HotkeyPreset = PresetOrder[ComboHotkey.SelectedIndex];
        }

        _settings.AutoSwitchEnabled = ChkAutoSwitch.IsChecked == true;

        _onSaveCallback(_settings);
        Hide();
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T item) where T : notnull
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], item))
                return i;
        }
        return -1;
    }
}
