using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GeminiUsageMonitor.Models;
using GeminiUsageMonitor.Services;

using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace GeminiUsageMonitor.Views;

public partial class OverlayWindow : Window
{
    private static readonly SolidColorBrush NormalCurrentBrush = CreateFrozenBrush(0xE8, 0xEA, 0xED);
    private static readonly SolidColorBrush AmberTextBrush = CreateFrozenBrush(0xFF, 0xB8, 0x30);
    private static readonly SolidColorBrush RedTextBrush = CreateFrozenBrush(0xFF, 0x5C, 0x5C);
    private static readonly SolidColorBrush WeeklyNormalBrush = CreateFrozenBrush(0xBD, 0xC1, 0xC6);

    private static readonly SolidColorBrush AmberBorderBrush = CreateFrozenBrush(0xD9, 0xFF, 0x9F, 0x1C);
    private static readonly SolidColorBrush RedBorderBrush = CreateFrozenBrush(0xE6, 0xFF, 0x3B, 0x30);
    private static readonly SolidColorBrush PillDefaultBorderBrush = CreateFrozenBrush(0x3D, 0xFF, 0xFF, 0xFF);

    private static readonly Color AmberShadowColor = Color.FromRgb(0xFF, 0x9F, 0x1C);
    private static readonly Color RedShadowColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    private readonly DispatcherTimer _bannerHideTimer;
    private nint _hwnd;

    public OverlayWindow()
    {
        InitializeComponent();

        _bannerHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(7)
        };
        _bannerHideTimer.Tick += (_, _) =>
        {
            _bannerHideTimer.Stop();
            InlineAlertBanner.Visibility = Visibility.Collapsed;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.MakeClickThroughToolWindow(_hwnd);
    }

    public void EnsureHandleCreated()
    {
        if (_hwnd != nint.Zero)
        {
            return;
        }

        // Ініціалізуємо HWND непомітно за межами екрана, щоб SetWindowPos міг працювати миттєво
        Width = 1;
        Height = 1;
        Left = -20000;
        Top = -20000;
        Show();
        Hide();
    }

    public void UpdateTargetBounds(Win32Interop.RECT? bounds)
    {
        EnsureHandleCreated();

        if (bounds is null)
        {
            if (Visibility == Visibility.Visible)
            {
                Hide();
            }
            return;
        }

        var rect = bounds.Value;

        if (Visibility != Visibility.Visible)
        {
            Show();
        }

        // Оновлюємо логічні розміри WPF з урахуванням DPI монітора, щоб верстка розтягувалась на все вікно
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double logicalWidth = rect.Width / scaleX;
        double logicalHeight = rect.Height / scaleY;

        Width = logicalWidth;
        Height = logicalHeight;

        // Якщо вікно вузьке (наприклад компактний оверлей), опускаємо капсулу трохи нижче заголовка.
        // У широкому вікні відступ справа 204 px залишає повністю відкритими і 3 системні кнопки (— □ ✕),
        // і 4-ту кнопку головного меню/гарячих клавіш Gemini (=) ліворуч від них.
        TopRightContainer.Margin = logicalWidth < 560
            ? new Thickness(0, 44, 14, 0)
            : new Thickness(0, 8, 204, 0);

        Win32Interop.SetWindowPos(
            _hwnd,
            Win32Interop.HWND_TOPMOST,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW | Win32Interop.SWP_NOOWNERZORDER);
    }

    public void UpdateUsageState(UsageSnapshot snapshot, HighlightLevel highlight, bool isPendingRefresh)
    {
        PendingDot.Visibility = isPendingRefresh ? Visibility.Visible : Visibility.Collapsed;

        if (!snapshot.IsAuthenticated)
        {
            CurrentUsageText.Text = "⚡ Увійдіть у Google (Трей)";
            CurrentUsageText.Foreground = AmberTextBrush;
            WeeklyUsageText.Text = "📅 --%";
            WeeklyUsageText.Foreground = WeeklyNormalBrush;
            ApplyFrameHighlight(HighlightLevel.None);
            return;
        }

        CurrentUsageText.Text = snapshot.CurrentUsagePercent.HasValue
            ? $"⚡ {snapshot.CurrentUsagePercent.Value}%"
            : "⚡ --%";

        WeeklyUsageText.Text = snapshot.WeeklyLimitPercent.HasValue
            ? $"📅 {snapshot.WeeklyLimitPercent.Value}%"
            : "📅 --%";

        // Колір тексту Поточного використання в капсулі відповідає рівню тривоги
        CurrentUsageText.Foreground = highlight switch
        {
            HighlightLevel.Red90 => RedTextBrush,
            HighlightLevel.Amber75 => AmberTextBrush,
            _ => NormalCurrentBrush
        };

        // Тижневий ліміт відображається ТІЛЬКИ на плашці (легкий колірний акцент тексту при 75%+/90%+, без підсвітки рамки)
        WeeklyUsageText.Foreground = snapshot.WeeklyLimitPercent switch
        {
            >= 90 => RedTextBrush,
            >= 75 => AmberTextBrush,
            _ => WeeklyNormalBrush
        };

        ApplyFrameHighlight(highlight);
    }

    public void ShowInlineAlert(AlertNotification alert)
    {
        InlineAlertTitle.Text = alert.IsCritical ? $"🚨 {alert.Title}" : $"⚠️ {alert.Title}";
        InlineAlertTitle.Foreground = alert.IsCritical ? RedTextBrush : AmberTextBrush;
        InlineAlertBanner.BorderBrush = alert.IsCritical ? RedBorderBrush : AmberBorderBrush;
        InlineAlertBody.Text = alert.Message;
        InlineAlertBanner.Visibility = Visibility.Visible;

        _bannerHideTimer.Stop();
        _bannerHideTimer.Start();
    }

    private void ApplyFrameHighlight(HighlightLevel highlight)
    {
        switch (highlight)
        {
            case HighlightLevel.Amber75:
                FrameGlowBorder.BorderBrush = AmberBorderBrush;
                FrameGlowBorder.BorderThickness = new Thickness(2.5);
                FrameGlowShadow.Color = AmberShadowColor;
                FrameGlowShadow.BlurRadius = 16;
                FrameGlowBorder.Opacity = 1.0;
                UsagePill.BorderBrush = AmberBorderBrush;
                break;

            case HighlightLevel.Red90:
                FrameGlowBorder.BorderBrush = RedBorderBrush;
                FrameGlowBorder.BorderThickness = new Thickness(3.0);
                FrameGlowShadow.Color = RedShadowColor;
                FrameGlowShadow.BlurRadius = 22;
                FrameGlowBorder.Opacity = 1.0;
                UsagePill.BorderBrush = RedBorderBrush;
                break;

            default:
                FrameGlowBorder.Opacity = 0.0;
                FrameGlowBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
                UsagePill.BorderBrush = PillDefaultBorderBrush;
                break;
        }
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
