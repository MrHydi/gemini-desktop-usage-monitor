using System.Diagnostics;
using System.Windows.Threading;

namespace GeminiUsageMonitor.Services;

public sealed class GeminiWindowTracker : IDisposable
{
    private readonly Win32Interop.WinEventDelegate _winEventProc;
    private readonly List<nint> _hooks = new();
    private readonly Dictionary<uint, (bool IsGemini, DateTime CheckedAt)> _pidCache = new();

    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _keyPollTimer;

    private nint _trackedHwnd = nint.Zero;
    private Win32Interop.RECT _lastBounds;
    private bool _hasLastBounds;

    private bool _wasEnterDown;
    private DateTime _lastEnterTriggeredUtc = DateTime.MinValue;

    public event Action<Win32Interop.RECT?>? GeminiBoundsChanged;
    public event Action? EnterSubmittedInGemini;

    public bool IsGeminiActive => _trackedHwnd != nint.Zero;

    public GeminiWindowTracker()
    {
        // Зберігаємо делегат у полі класу, щоб GC не зібрав його під час роботи unmanaged hook
        _winEventProc = OnWinEvent;

        _syncTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _syncTimer.Tick += (_, _) => RefreshTargetWindow();

        _keyPollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _keyPollTimer.Tick += OnKeyPollTick;
    }

    public void Start()
    {
        uint flags = Win32Interop.WINEVENT_OUTOFCONTEXT | Win32Interop.WINEVENT_SKIPOWNPROCESS;

        AddHook(Win32Interop.EVENT_SYSTEM_FOREGROUND, Win32Interop.EVENT_SYSTEM_FOREGROUND, flags);
        AddHook(Win32Interop.EVENT_SYSTEM_MINIMIZESTART, Win32Interop.EVENT_SYSTEM_MINIMIZEEND, flags);
        AddHook(Win32Interop.EVENT_OBJECT_SHOW, Win32Interop.EVENT_OBJECT_HIDE, flags);
        AddHook(Win32Interop.EVENT_OBJECT_LOCATIONCHANGE, Win32Interop.EVENT_OBJECT_LOCATIONCHANGE, flags);

        _syncTimer.Start();
        RefreshTargetWindow();
    }

    private void AddHook(uint minEvent, uint maxEvent, uint flags)
    {
        nint hook = Win32Interop.SetWinEventHook(minEvent, maxEvent, nint.Zero, _winEventProc, 0, 0, flags);
        if (hook != nint.Zero)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType == Win32Interop.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (idObject != Win32Interop.OBJID_WINDOW || hwnd != _trackedHwnd)
            {
                return;
            }
        }

        RefreshTargetWindow();
    }

    private void RefreshTargetWindow()
    {
        nint fgHwnd = Win32Interop.GetForegroundWindow();

        if (fgHwnd != nint.Zero && IsGeminiProcessWindow(fgHwnd))
        {
            if (Win32Interop.TryGetVisibleWindowBounds(fgHwnd, out var fgBounds))
            {
                // Головне вікно чату або швидке вікно Alt + Space мають нормальний розмір (>= 340x240).
                // Дрібні допоміжні вікна Electron (пошук Ctrl+F, перемикач акаунтів) не повинні перетягувати рамку на себе.
                if (fgBounds.Width >= 340 && fgBounds.Height >= 240)
                {
                    _trackedHwnd = fgHwnd;
                    UpdateBounds(fgBounds);
                    EnsureKeyPolling(true);
                    return;
                }

                // Якщо у фокусі маленьке дочірнє віконце самого Gemini (наприклад Find Bar),
                // залишаємо рамку на основному відстежуваному вікні Gemini, якщо воно досі видиме.
                if (_trackedHwnd != nint.Zero && Win32Interop.TryGetVisibleWindowBounds(_trackedHwnd, out var mainBounds))
                {
                    UpdateBounds(mainBounds);
                    EnsureKeyPolling(true);
                    return;
                }
            }
        }

        // Якщо активне інше вікно або вікно Gemini сховано/згорнуто
        _trackedHwnd = nint.Zero;
        EnsureKeyPolling(false);
        ClearBounds();
    }

    private void EnsureKeyPolling(bool active)
    {
        if (active && !_keyPollTimer.IsEnabled)
        {
            // Скидаємо поточний стан клавіші при вході у фокус, щоб не спрацював Enter, яким щойно відкрили вікно
            _wasEnterDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_RETURN) & 0x8000) != 0;
            _keyPollTimer.Start();
        }
        else if (!active && _keyPollTimer.IsEnabled)
        {
            _keyPollTimer.Stop();
            _wasEnterDown = false;
        }
    }

    private void OnKeyPollTick(object? sender, EventArgs e)
    {
        if (_trackedHwnd == nint.Zero)
        {
            return;
        }

        bool isEnterDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_RETURN) & 0x8000) != 0;
        bool isShiftDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_SHIFT) & 0x8000) != 0;

        // Реагуємо лише на натискання Enter БЕЗ Shift (бо Shift+Enter просто переносить рядок)
        if (isEnterDown && !_wasEnterDown && !isShiftDown)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastEnterTriggeredUtc).TotalMilliseconds >= 600)
            {
                _lastEnterTriggeredUtc = now;
                EnterSubmittedInGemini?.Invoke();
            }
        }

        _wasEnterDown = isEnterDown;
    }

    private void UpdateBounds(Win32Interop.RECT newBounds)
    {
        if (_hasLastBounds &&
            _lastBounds.Left == newBounds.Left &&
            _lastBounds.Top == newBounds.Top &&
            _lastBounds.Right == newBounds.Right &&
            _lastBounds.Bottom == newBounds.Bottom)
        {
            return;
        }

        _lastBounds = newBounds;
        _hasLastBounds = true;
        GeminiBoundsChanged?.Invoke(newBounds);
    }

    private void ClearBounds()
    {
        if (!_hasLastBounds)
        {
            return;
        }

        _hasLastBounds = false;
        _lastBounds = default;
        GeminiBoundsChanged?.Invoke(null);
    }

    private bool IsGeminiProcessWindow(nint hwnd)
    {
        Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (_pidCache.TryGetValue(pid, out var cached) && (now - cached.CheckedAt).TotalSeconds < 30)
        {
            return cached.IsGemini;
        }

        bool isGemini = false;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            isGemini = string.Equals(name, "Gemini", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            isGemini = false;
        }

        if (_pidCache.Count > 256)
        {
            _pidCache.Clear();
        }

        _pidCache[pid] = (isGemini, now);
        return isGemini;
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _keyPollTimer.Stop();

        foreach (nint hook in _hooks)
        {
            Win32Interop.UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }
}
