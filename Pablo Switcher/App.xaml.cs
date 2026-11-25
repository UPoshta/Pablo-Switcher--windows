using Microsoft.Win32;
using Pablo_Switcher.Models;
using Pablo_Switcher.Services;
using Pablo_Switcher.Views;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pablo_Switcher;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    private AppSettings _settings = null!;
    private XmlI18nManager _i18n = null!;
    private PrefixLanguageDetector _detector = null!;
    private WindowsInputBridge _bridge = null!;
    private AsyncKeyboardEngine _engine = null!;
    private ForegroundWindowMonitor _windowMonitor = null!;
    private SettingsWindow _settingsWindow = null!;
    private AboutWindow? _aboutWindow;

    private Forms.NotifyIcon _trayIcon = null!;
    private Icon? _currentTrayIconHandle;

    private Forms.ToolStripMenuItem _statusItem = null!;
    private Forms.ToolStripMenuItem _lastActionItem = null!;
    private Forms.ToolStripMenuItem _statsItem = null!;
    private Forms.ToolStripMenuItem _toggleItem = null!;
    private Forms.ToolStripMenuItem _forceConvertItem = null!;
    private Forms.ToolStripMenuItem _autostartItem = null!;
    private Forms.ToolStripMenuItem _settingsItem = null!;
    private Forms.ToolStripMenuItem _aboutItem = null!;
    private Forms.ToolStripMenuItem _quitItem = null!;

    private int _switchCount;
    private (LangCode Lang, string Orig, string Conv, string Mode)? _lastSwitchInfo;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        bool enableTrace = true;
#else
    bool enableTrace = e.Args.Contains("--console", StringComparer.OrdinalIgnoreCase);
#endif
        TraceLogger.Initialize(enableTrace);

        // Знімаємо клавіатурний хук навіть при завершенні сесії Windows або непередбаченій помилці
        SessionEnding += (_, _) => CleanupResources();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _bridge?.Dispose();

        // Захист від повторного запуску другої копії програми
        _singleInstanceMutex = new Mutex(true, "Global\\OurTube.AutoSwitcher.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _i18n = new XmlI18nManager
        {
            Language = _settings.UiLanguage
        };

        string vocabPath = Path.Combine(AppContext.BaseDirectory, "Resources", "wordfreq.bin");
        _detector = new PrefixLanguageDetector(
            vocabBinPath: vocabPath,
            customVocabPath: AppPaths.CustomVocabPath,
            minPrefixLen: 3);
        _bridge = new WindowsInputBridge();

        _engine = new AsyncKeyboardEngine(
            detector: _detector,
            bridge: _bridge,
            hotkeyPreset: _settings.HotkeyPreset,
            enabled: _settings.AutoSwitchEnabled,
            onSwitchCallback: HandleSwitchEvent);

        _settingsWindow = new SettingsWindow(_i18n, _settings, OnSettingsSaved);

        InitializeTrayIcon();
        RefreshMenuTitles();

        _windowMonitor = new ForegroundWindowMonitor(() =>
        {
            _engine.NotifyActiveAppChanged(_bridge.GetForegroundProcessName());
        });

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _engine.Start();
    }

    private void CleanupResources()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        TraceLogger.Shutdown();
        _windowMonitor?.Dispose();
        _engine?.Dispose();
        _bridge?.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _currentTrayIconHandle?.Dispose();
    }
    private void InitializeTrayIcon()
    {
        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _lastActionItem = new Forms.ToolStripMenuItem { Enabled = false };
        _statsItem = new Forms.ToolStripMenuItem { Enabled = false };

        _toggleItem = new Forms.ToolStripMenuItem();
        _toggleItem.Click += (_, _) => OnToggleActive();

        _forceConvertItem = new Forms.ToolStripMenuItem();
        _forceConvertItem.Click += (_, _) => _engine.ForceConvertWordOrSelection();

        _autostartItem = new Forms.ToolStripMenuItem
        {
            Checked = AutostartService.IsAutostartEnabled()
        };
        _autostartItem.Click += (_, _) => OnToggleAutostart();

        _settingsItem = new Forms.ToolStripMenuItem();
        _settingsItem.Click += (_, _) => _settingsWindow.ShowAndActivate();

        _aboutItem = new Forms.ToolStripMenuItem();
        _aboutItem.Click += (_, _) => OpenAboutWindow();

        _quitItem = new Forms.ToolStripMenuItem();
        _quitItem.Click += (_, _) => ExitApplication();

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.AddRange(
        [
            _statusItem,
            _lastActionItem,
            _statsItem,
            new Forms.ToolStripSeparator(),
            _toggleItem,
            _forceConvertItem,
            _autostartItem,
            new Forms.ToolStripSeparator(),
            _settingsItem,
            _aboutItem,
            new Forms.ToolStripSeparator(),
            _quitItem
        ]);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = AppInfo.AppName,
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        // Подвійний клік по іконці в треї відкриває налаштування
        _trayIcon.DoubleClick += (_, _) => _settingsWindow.ShowAndActivate();

        UpdateTrayIconTheme();
    }

    private void UpdateTrayIconTheme()
    {
        Icon newIcon = TrayIconService.CreateTrayIcon();
        _trayIcon.Icon = newIcon;

        _currentTrayIconHandle?.Dispose();
        _currentTrayIconHandle = newIcon;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(UpdateTrayIconTheme);
        }
    }

    private void RefreshMenuTitles()
    {
        _statusItem.Text = _engine.Enabled
            ? _i18n.T("status_active")
            : _i18n.T("status_paused");

        if (_lastSwitchInfo is null)
        {
            _lastActionItem.Text = _i18n.T("last_action_none");
        }
        else
        {
            var (lang, orig, conv, modeKey) = _lastSwitchInfo.Value;
            string langBadge = lang == LangCode.Uk ? "UA" : "EN";
            string modeStr = _i18n.T($"mode_{modeKey}");
            string shortOrig = orig.Length <= 18 ? orig : orig[..15] + "…";
            string shortConv = conv.Length <= 18 ? conv : conv[..15] + "…";

            _lastActionItem.Text = _i18n.T(
                "last_action_fmt",
                ("mode", modeStr),
                ("orig", shortOrig),
                ("conv", shortConv),
                ("lang", langBadge));
        }

        _statsItem.Text = _i18n.T("stats_fmt", ("count", _switchCount));
        _toggleItem.Text = _engine.Enabled
            ? _i18n.T("menu_pause")
            : _i18n.T("menu_resume");

        _autostartItem.Text = _i18n.T("menu_autostart");
        _autostartItem.Checked = AutostartService.IsAutostartEnabled();

        string hotkeyStr = _i18n.HotkeyLabel(_settings.HotkeyPreset);
        _forceConvertItem.Text = _i18n.T("menu_force_convert", ("hotkey", hotkeyStr));

        _settingsItem.Text = _i18n.T("menu_settings");
        _aboutItem.Text = _i18n.T("menu_about");
        _quitItem.Text = _i18n.T("menu_quit");
    }

    private void HandleSwitchEvent(LangCode lang, string original, string converted, string mode)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (mode == "auto")
            {
                _switchCount++;
            }
            _lastSwitchInfo = (lang, original, converted, mode);
            RefreshMenuTitles();
        });
    }

    private void OnToggleActive()
    {
        _engine.Enabled = !_engine.Enabled;
        _settings.AutoSwitchEnabled = _engine.Enabled;
        _settings.Save();
        RefreshMenuTitles();
    }

    private void OnToggleAutostart()
    {
        bool targetState = !_autostartItem.Checked;
        if (AutostartService.SetAutostart(targetState))
        {
            _autostartItem.Checked = targetState;
        }
        else
        {
            MessageBox.Show(
                "Ця опція працює лише у зібраній версії програми (AutoSwitcher.exe).",
                "Автозапуск недоступний",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnSettingsSaved(AppSettings updatedSettings)
    {
        updatedSettings.Save();
        _i18n.Language = updatedSettings.UiLanguage;
        _engine.HotkeyPreset = updatedSettings.HotkeyPreset;
        _engine.Enabled = updatedSettings.AutoSwitchEnabled;
        RefreshMenuTitles();
    }

    private void OpenAboutWindow()
    {
        if (_aboutWindow is { IsVisible: true })
        {
            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow(_i18n);
        _aboutWindow.Show();
    }

    private void ExitApplication()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _windowMonitor.Dispose();
        _engine.Dispose();
        _bridge.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _currentTrayIconHandle?.Dispose();

        _settingsWindow.ForceClose();
        _aboutWindow?.Close();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        Shutdown();
    }
}
