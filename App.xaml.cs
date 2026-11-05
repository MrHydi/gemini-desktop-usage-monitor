using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using GeminiUsageMonitor.Models;
using GeminiUsageMonitor.Services;
using GeminiUsageMonitor.Views;

using Application = System.Windows.Application;

namespace GeminiUsageMonitor;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private NotifyIcon? _trayIcon;
    private Icon? _currentIconHandle;

    private ToolStripMenuItem? _statusMenuItem;
    private ToolStripMenuItem? _autoStartMenuItem;

    private WebViewHostWindow? _webViewWindow;
    private OverlayWindow? _overlayWindow;
    private GeminiWindowTracker? _windowTracker;
    private UsageScraperService? _scraperService;
    private LimitAlertStateMachine? _alertStateMachine;

    private bool _isSimulationMode;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\GeminiUsageMonitor_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _alertStateMachine = new LimitAlertStateMachine();
        _overlayWindow = new OverlayWindow();
        _overlayWindow.EnsureHandleCreated();

        _webViewWindow = new WebViewHostWindow();
        _scraperService = new UsageScraperService(_webViewWindow);
        _windowTracker = new GeminiWindowTracker();

        InitializeTrayIcon();

        // Підписуємось на події трекера вікна Gemini та клавіші Enter
        _windowTracker.GeminiBoundsChanged += bounds =>
        {
            Dispatcher.InvokeAsync(() => _overlayWindow.UpdateTargetBounds(bounds));
        };

        _windowTracker.EnterSubmittedInGemini += () =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isSimulationMode)
                {
                    _scraperService.SchedulePostEnterRefresh();
                }
            });
        };

        // Підписуємось на оновлення даних зі сторінки лімітів
        _scraperService.SnapshotUpdated += snapshot =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isSimulationMode)
                {
                    ApplySnapshot(snapshot);
                }
            });
        };

        _scraperService.PendingStateChanged += isPending =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isSimulationMode && _scraperService is not null && _alertStateMachine is not null)
                {
                    _overlayWindow.UpdateUsageState(
                        _scraperService.LatestSnapshot,
                        _alertStateMachine.CurrentHighlight,
                        isPending);
                }
            });
        };

        // Початковий рендер і запуск трекера вікон
        ApplySnapshot(UsageSnapshot.Initial());
        _windowTracker.Start();

        await _scraperService.InitializeAsync();
    }

    private void InitializeTrayIcon()
    {
        var menu = new ContextMenuStrip();

        _statusMenuItem = new ToolStripMenuItem("⚡ Поточне: --%  |  📅 Тижневий: --%")
        {
            Enabled = false
        };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        var refreshItem = new ToolStripMenuItem("🔄 Оновити ліміти зараз");
        refreshItem.Click += async (_, _) =>
        {
            _isSimulationMode = false;
            if (_scraperService is not null)
            {
                await _scraperService.RefreshNowAsync("Ручне оновлення з трею");
            }
        };
        menu.Items.Add(refreshItem);

        var openBrowserItem = new ToolStripMenuItem("🌐 Акаунт Google / Сторінка лімітів...");
        openBrowserItem.Click += (_, _) => _webViewWindow?.ShowInteractive();
        menu.Items.Add(openBrowserItem);

        // Підменю для швидкої перевірки вигляду підсвітки та сповіщень без витрачання реальної квоти
        var testMenu = new ToolStripMenuItem("🧪 Тест попередження");

        var testAmberItem = new ToolStripMenuItem("Перевірити 78% (Жовтогаряча підсвітка + сповіщення)");
        testAmberItem.Click += (_, _) => RunSimulation(current: 78, weekly: 41);
        testMenu.DropDownItems.Add(testAmberItem);

        var testRedItem = new ToolStripMenuItem("Перевірити 92% (Червона підсвітка + критичне сповіщення)");
        testRedItem.Click += (_, _) => RunSimulation(current: 92, weekly: 41);
        testMenu.DropDownItems.Add(testRedItem);

        var testWeeklyOnlyItem = new ToolStripMenuItem("Перевірити Тижневий 77% при Поточному 30% (Без рамки, лише плашка + сповіщення)");
        testWeeklyOnlyItem.Click += (_, _) => RunSimulation(current: 30, weekly: 77);
        testMenu.DropDownItems.Add(testWeeklyOnlyItem);

        testMenu.DropDownItems.Add(new ToolStripSeparator());

        var exitSimItem = new ToolStripMenuItem("Повернути реальні дані");
        exitSimItem.Click += (_, _) =>
        {
            _isSimulationMode = false;
            _alertStateMachine = new LimitAlertStateMachine();
            if (_scraperService is not null)
            {
                ApplySnapshot(_scraperService.LatestSnapshot);
            }
        };
        testMenu.DropDownItems.Add(exitSimItem);

        menu.Items.Add(testMenu);
        menu.Items.Add(new ToolStripSeparator());

        _autoStartMenuItem = new ToolStripMenuItem("Запускати разом із Windows")
        {
            Checked = StartupRegistryHelper.IsAutoStartEnabled(),
            CheckOnClick = true
        };
        _autoStartMenuItem.CheckedChanged += (_, _) =>
        {
            StartupRegistryHelper.SetAutoStart(_autoStartMenuItem.Checked);
        };
        menu.Items.Add(_autoStartMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Вихід");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        _currentIconHandle = TrayIconFactory.CreateStatusIcon(HighlightLevel.None, null);
        _trayIcon = new NotifyIcon
        {
            Icon = _currentIconHandle,
            Text = "Монітор лімітів Gemini",
            ContextMenuStrip = menu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => _webViewWindow?.ShowInteractive();
    }

    private void RunSimulation(int current, int weekly)
    {
        _isSimulationMode = true;
        // Для чистоти демо створюємо свіжий стан тригерів при кожному тесті
        _alertStateMachine = new LimitAlertStateMachine();

        var simSnapshot = new UsageSnapshot(
            CurrentUsagePercent: current,
            WeeklyLimitPercent: weekly,
            Timestamp: DateTime.Now,
            IsAuthenticated: true,
            StatusMessage: "Тестовий режим");

        ApplySnapshot(simSnapshot);
    }

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        if (_alertStateMachine is null || _overlayWindow is null)
        {
            return;
        }

        var (highlight, notifications) = _alertStateMachine.Evaluate(snapshot);
        bool isPending = !_isSimulationMode && (_scraperService?.IsPendingPostEnterRefresh ?? false);

        _overlayWindow.UpdateUsageState(snapshot, highlight, isPending);
        UpdateTrayVisuals(snapshot, highlight);

        foreach (var alert in notifications)
        {
            // 1. Системне сповіщення Windows (Toast через NotifyIcon)
            _trayIcon?.ShowBalloonTip(
                timeout: 6000,
                tipTitle: alert.Title,
                tipText: alert.Message,
                tipIcon: alert.IsCritical ? ToolTipIcon.Error : ToolTipIcon.Warning);

            // 2. Дублюючий банер на оверлеї (щоб не пропустити, якщо у Windows увімкнено Focus Assist)
            _overlayWindow.ShowInlineAlert(alert);
        }
    }

    private void UpdateTrayVisuals(UsageSnapshot snapshot, HighlightLevel highlight)
    {
        string cur = snapshot.CurrentUsagePercent.HasValue ? $"{snapshot.CurrentUsagePercent.Value}%" : "--%";
        string wk = snapshot.WeeklyLimitPercent.HasValue ? $"{snapshot.WeeklyLimitPercent.Value}%" : "--%";
        string prefix = _isSimulationMode ? "[ТЕСТ] " : string.Empty;

        if (_statusMenuItem is not null)
        {
            _statusMenuItem.Text = snapshot.IsAuthenticated
                ? $"{prefix}⚡ Поточне: {cur}  |  📅 Тижневий: {wk}"
                : "⚠️ Потрібен вхід у Google-акаунт";
        }

        if (_trayIcon is not null)
        {
            string tooltip = snapshot.IsAuthenticated
                ? $"{prefix}Gemini — ⚡ {cur} | 📅 {wk}"
                : "Gemini Монітор — Увійдіть у Google";

            // Обмеження Win32 NotifyIcon.Text — до 63 символів
            _trayIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;

            var newIcon = TrayIconFactory.CreateStatusIcon(highlight, snapshot.CurrentUsagePercent);
            var oldIcon = _currentIconHandle;
            _trayIcon.Icon = newIcon;
            _currentIconHandle = newIcon;
            oldIcon?.Dispose();
        }
    }

    private void ExitApplication()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _currentIconHandle?.Dispose();
        _windowTracker?.Dispose();
        _scraperService?.Dispose();

        if (_webViewWindow is not null)
        {
            _webViewWindow.AllowShutdownClose();
            _webViewWindow.Close();
        }

        _overlayWindow?.Close();
        _singleInstanceMutex?.Dispose();

        Shutdown();
    }
}
