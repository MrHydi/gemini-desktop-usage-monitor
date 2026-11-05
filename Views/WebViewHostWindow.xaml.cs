using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using GeminiUsageMonitor.Models;
using GeminiUsageMonitor.Services;
using Microsoft.Web.WebView2.Core;

namespace GeminiUsageMonitor.Views;

public partial class WebViewHostWindow : Window
{
    private nint _hwnd;
    private bool _allowRealClose;
    private bool _isInteractiveMode;

    public event Func<Task>? ManualRefreshRequested;

    public CoreWebView2? CoreBrowser => Browser.CoreWebView2;
    public bool IsInteractiveMode => _isInteractiveMode;

    public WebViewHostWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (!_isInteractiveMode)
        {
            Win32Interop.MakeBackgroundToolWindow(_hwnd);
        }
    }

    public async Task InitializeWebViewAsync()
    {
        // Запускаємо вікно у прихованому фоновому стані за межами екрана,
        // щоб Chromium не "заморожував" рендеринг Angular-компонентів Gemini
        ParkInBackground();

        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiUsageMonitor",
            "WebView2Profile");

        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await Browser.EnsureCoreWebView2Async(env);

        Browser.CoreWebView2.IsMuted = true;
        Browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
    }

    public void ShowInteractive()
    {
        _isInteractiveMode = true;

        if (!IsVisible)
        {
            Show();
        }

        Opacity = 1.0;
        WindowState = WindowState.Normal;
        Width = 1020;
        Height = 740;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        if (_hwnd != nint.Zero)
        {
            Win32Interop.RestoreNormalAppWindow(_hwnd);
        }

        Activate();
        Focus();
    }

    public void ParkInBackground()
    {
        _isInteractiveMode = false;
        WindowState = WindowState.Normal;
        Width = 1280;
        Height = 820;
        Left = -18000;
        Top = -18000;
        Opacity = 0.01;

        if (!IsVisible)
        {
            Show();
        }

        if (_hwnd != nint.Zero)
        {
            Win32Interop.MakeBackgroundToolWindow(_hwnd);
        }
    }

    public void UpdateHeaderStatus(UsageSnapshot snapshot)
    {
        if (!snapshot.IsAuthenticated)
        {
            HeaderStatusText.Text = "⚠️ Потрібен вхід у Google-акаунт нижче";
            return;
        }

        string cur = snapshot.CurrentUsagePercent.HasValue ? $"{snapshot.CurrentUsagePercent.Value}%" : "--%";
        string wk = snapshot.WeeklyLimitPercent.HasValue ? $"{snapshot.WeeklyLimitPercent.Value}%" : "--%";
        HeaderStatusText.Text = $"Поточне: ⚡ {cur}  |  Тижневий: 📅 {wk}   •   {snapshot.StatusMessage} ({snapshot.Timestamp:HH:mm:ss})";
    }

    public void AllowShutdownClose()
    {
        _allowRealClose = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowRealClose)
        {
            e.Cancel = true;
            ParkInBackground();
            return;
        }

        base.OnClosing(e);
    }

    private async void OnTestScrapeClick(object sender, RoutedEventArgs e)
    {
        TestScrapeButton.IsEnabled = false;
        try
        {
            if (ManualRefreshRequested is not null)
            {
                await ManualRefreshRequested.Invoke();
            }
        }
        finally
        {
            TestScrapeButton.IsEnabled = true;
        }
    }

    private void OnHideToTrayClick(object sender, RoutedEventArgs e)
    {
        ParkInBackground();
    }
}
