using System.Text.Json;
using System.Windows.Threading;
using GeminiUsageMonitor.Models;
using GeminiUsageMonitor.Views;
using Microsoft.Web.WebView2.Core;

namespace GeminiUsageMonitor.Services;

/// <summary>
/// Відповідає за фонове зчитування розділу "Ліміти використання" у Gemini через WebView2
/// та реалізує адаптивний таймер оновлення після Enter: 10 с -> 20 с -> 40 с -> 80 с.
/// </summary>
public sealed class UsageScraperService : IDisposable
{
    public const int BaseEnterDelaySeconds = 10;
    public const int MaxEnterBackoffAttempts = 4; // 10 -> 20 -> 40 -> 80 с

    private const string GeminiAppUrl = "https://gemini.google.com/app";

    private readonly WebViewHostWindow _hostWindow;
    private readonly SemaphoreSlim _scrapeLock = new(1, 1);
    private readonly DispatcherTimer _idleTimer;

    private CancellationTokenSource? _postEnterCts;
    private string? _discoveredUsageUrl;
    private UsageSnapshot _latestSnapshot = UsageSnapshot.Initial();
    private bool _hasPromptedLoginOnce;

    public event Action<UsageSnapshot>? SnapshotUpdated;
    public event Action<bool>? PendingStateChanged;

    public UsageSnapshot LatestSnapshot => _latestSnapshot;
    public bool IsPendingPostEnterRefresh { get; private set; }

    public UsageScraperService(WebViewHostWindow hostWindow)
    {
        _hostWindow = hostWindow;
        _hostWindow.ManualRefreshRequested += () => RefreshNowAsync("Ручне оновлення");

        // Тихе фонове оновлення раз на 12 хвилин у стані спокою (для відстеження 5-годинного відновлення квоти)
        _idleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(12)
        };
        _idleTimer.Tick += async (_, _) =>
        {
            if (!IsPendingPostEnterRefresh)
            {
                await RefreshNowAsync("Фонове оновлення");
            }
        };
    }

    public async Task InitializeAsync()
    {
        await _hostWindow.InitializeWebViewAsync();

        if (_hostWindow.CoreBrowser is not null)
        {
            _hostWindow.CoreBrowser.NavigationCompleted += OnBrowserNavigationCompleted;
        }

        _idleTimer.Start();

        // Перше зчитування при старті програми
        await RefreshNowAsync("Початкова перевірка");
    }

    private async void OnBrowserNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_latestSnapshot.IsAuthenticated || _hostWindow.CoreBrowser is null)
        {
            return;
        }

        string url = _hostWindow.CoreBrowser.Source ?? string.Empty;
        if (url.StartsWith("https://gemini.google.com/", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            // Користувач щойно успішно увійшов у Google-акаунт — автоматично зчитуємо ліміти
            await Task.Delay(1200);
            await RefreshNowAsync("Автоматично після входу", skipReload: true);
        }
    }

    /// <summary>
    /// Викликається при натисканні Enter (без Shift) у вікні Gemini.exe.
    /// Запускає цикл: чекаємо 10 с -> якщо дані ті самі, множимо таймер на 2 (20 -> 40 -> 80 с),
    /// щойно дані оновились — скидаємо таймер назад до 10 с.
    /// </summary>
    public void SchedulePostEnterRefresh()
    {
        _postEnterCts?.Cancel();
        _postEnterCts?.Dispose();
        _postEnterCts = new CancellationTokenSource();

        CancellationToken ct = _postEnterCts.Token;
        int? baselineCurrent = _latestSnapshot.CurrentUsagePercent;
        int? baselineWeekly = _latestSnapshot.WeeklyLimitPercent;

        _ = RunExponentialRefreshLoopAsync(baselineCurrent, baselineWeekly, ct);
    }

    private async Task RunExponentialRefreshLoopAsync(int? baselineCurrent, int? baselineWeekly, CancellationToken ct)
    {
        int delaySeconds = BaseEnterDelaySeconds;
        SetPendingState(true);

        try
        {
            for (int attempt = 1; attempt <= MaxEnterBackoffAttempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);

                var updated = await RefreshNowAsync($"Перевірка після відповіді (#{attempt}, {delaySeconds} с)");

                if (!updated.IsAuthenticated)
                {
                    break;
                }

                bool statsChanged =
                    updated.CurrentUsagePercent != baselineCurrent ||
                    updated.WeeklyLimitPercent != baselineWeekly;

                if (statsChanged)
                {
                    // Дані оновились — цикл завершено, базовий таймер для наступного разу знову 10 с
                    break;
                }

                // Дані ще не оновились (Gemini ще генерує відповідь або запит був < 1%): множимо таймер на 2
                delaySeconds *= 2;
            }
        }
        catch (OperationCanceledException)
        {
            // Користувач надіслав нове повідомлення — запущено новий цикл із 10 с
        }
        finally
        {
            if (_postEnterCts is not null && _postEnterCts.Token == ct)
            {
                SetPendingState(false);
            }
        }
    }

    public async Task<UsageSnapshot> RefreshNowAsync(string reason, bool skipReload = false)
    {
        if (!await _scrapeLock.WaitAsync(0))
        {
            // Якщо прямо зараз вже йде зчитування — дочекаємося його завершення
            await _scrapeLock.WaitAsync();
            _scrapeLock.Release();
            return _latestSnapshot;
        }

        try
        {
            var core = _hostWindow.CoreBrowser;
            if (core is null)
            {
                return _latestSnapshot;
            }

            string currentSource = core.Source ?? string.Empty;
            if (_hostWindow.IsInteractiveMode &&
                currentSource.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) &&
                reason != "Ручне оновлення")
            {
                // Не перериваємо процес входу користувача (введення пароля / 2FA)
                return _latestSnapshot;
            }

            if (!skipReload)
            {
                string targetUrl = _discoveredUsageUrl ?? GeminiAppUrl;
                await NavigateOrReloadAsync(core, targetUrl);
            }

            // Даємо SPA-додатку Gemini час відмалювати інтерфейс і (за потреби) відкриваємо меню "Ліміти використання"
            bool clickedSettingsThisRun = false;
            bool clickedUsageThisRun = false;

            for (int step = 0; step < 14; step++)
            {
                await Task.Delay(550);

                string jsonResult = await core.ExecuteScriptAsync(BuildProbeAndExtractScript(
                    allowClickSettings: !clickedSettingsThisRun,
                    allowClickUsage: !clickedUsageThisRun));

                using var outerDoc = JsonDocument.Parse(jsonResult);
                string? innerJson = outerDoc.RootElement.ValueKind == JsonValueKind.String
                    ? outerDoc.RootElement.GetString()
                    : outerDoc.RootElement.GetRawText();

                if (string.IsNullOrWhiteSpace(innerJson))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(innerJson);
                var root = doc.RootElement;

                string state = root.GetProperty("state").GetString() ?? "loading";

                if (state == "unauthenticated")
                {
                    var unauthSnap = new UsageSnapshot(
                        CurrentUsagePercent: null,
                        WeeklyLimitPercent: null,
                        Timestamp: DateTime.Now,
                        IsAuthenticated: false,
                        StatusMessage: "Потрібен вхід у Google-акаунт");

                    PublishSnapshot(unauthSnap);

                    if (!_hasPromptedLoginOnce)
                    {
                        _hasPromptedLoginOnce = true;
                        _hostWindow.ShowInteractive();
                    }

                    return unauthSnap;
                }

                if (root.TryGetProperty("action", out var actionProp))
                {
                    string? action = actionProp.GetString();
                    if (action == "clicked_settings")
                    {
                        clickedSettingsThisRun = true;
                    }
                    else if (action == "clicked_usage")
                    {
                        clickedUsageThisRun = true;
                    }
                }

                if (state == "extracted")
                {
                    int? cur = root.TryGetProperty("current", out var curEl) && curEl.ValueKind == JsonValueKind.Number
                        ? Math.Clamp(curEl.GetInt32(), 0, 100)
                        : null;

                    int? wk = root.TryGetProperty("weekly", out var wkEl) && wkEl.ValueKind == JsonValueKind.Number
                        ? Math.Clamp(wkEl.GetInt32(), 0, 100)
                        : null;

                    string? currentUrl = root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(currentUrl) &&
                        currentUrl.StartsWith("https://gemini.google.com/", StringComparison.OrdinalIgnoreCase) &&
                        !currentUrl.EndsWith("/app", StringComparison.OrdinalIgnoreCase) &&
                        !currentUrl.EndsWith("/app/", StringComparison.OrdinalIgnoreCase))
                    {
                        _discoveredUsageUrl = currentUrl;
                    }

                    string? rawSnippet = root.TryGetProperty("snippet", out var snipEl) ? snipEl.GetString() : null;

                    if (cur.HasValue || wk.HasValue)
                    {
                        _hasPromptedLoginOnce = false;
                        var okSnap = new UsageSnapshot(
                            CurrentUsagePercent: cur ?? _latestSnapshot.CurrentUsagePercent,
                            WeeklyLimitPercent: wk ?? _latestSnapshot.WeeklyLimitPercent,
                            Timestamp: DateTime.Now,
                            IsAuthenticated: true,
                            StatusMessage: reason,
                            RawDebugText: rawSnippet);

                        PublishSnapshot(okSnap);
                        return okSnap;
                    }
                }
            }

            // Якщо не вдалося знайти числа за 14 кроків (наприклад, повільний інтернет), зберігаємо попередні значення зі статусом
            var fallbackSnap = _latestSnapshot with
            {
                Timestamp = DateTime.Now,
                StatusMessage = "Не вдалося знайти шкали лімітів (перевірте вікно WebView у треї)"
            };
            PublishSnapshot(fallbackSnap);
            return fallbackSnap;
        }
        catch (Exception ex)
        {
            var errSnap = _latestSnapshot with
            {
                Timestamp = DateTime.Now,
                StatusMessage = $"Помилка зчитування: {ex.Message}"
            };
            PublishSnapshot(errSnap);
            return errSnap;
        }
        finally
        {
            _scrapeLock.Release();
        }
    }

    private static async Task NavigateOrReloadAsync(CoreWebView2 core, string targetUrl)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }

        core.NavigationCompleted += Handler;

        string currentUrl = core.Source ?? string.Empty;
        if (currentUrl.StartsWith("https://gemini.google.com/", StringComparison.OrdinalIgnoreCase))
        {
            core.Reload();
        }
        else
        {
            core.Navigate(targetUrl);
        }

        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        core.NavigationCompleted -= Handler;
    }

    private void PublishSnapshot(UsageSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        _hostWindow.UpdateHeaderStatus(snapshot);
        SnapshotUpdated?.Invoke(snapshot);
    }

    private void SetPendingState(bool pending)
    {
        if (IsPendingPostEnterRefresh == pending)
        {
            return;
        }

        IsPendingPostEnterRefresh = pending;
        PendingStateChanged?.Invoke(pending);
    }

    private static string BuildProbeAndExtractScript(bool allowClickSettings, bool allowClickUsage)
    {
        string clickSettingsFlag = allowClickSettings ? "true" : "false";
        string clickUsageFlag = allowClickUsage ? "true" : "false";

        return $$"""
        (() => {
            const allowClickSettings = {{clickSettingsFlag}};
            const allowClickUsage = {{clickUsageFlag}};

            const host = (location.hostname || '').toLowerCase();
            if (host.includes('accounts.google.com') || host.includes('workspace.google.com')) {
                return JSON.stringify({ state: 'unauthenticated', url: location.href });
            }

            const isVisible = (el) => {
                if (!el) return false;
                const style = window.getComputedStyle(el);
                if (style.display === 'none' || style.visibility === 'hidden' || parseFloat(style.opacity) === 0) {
                    return false;
                }
                const rect = el.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            };

            const signInAnchors = Array.from(document.querySelectorAll(
                'a[href*="accounts.google.com/ServiceLogin"], a[href*="accounts.google.com/AccountChooser"], a[aria-label="Sign in"], a[aria-label="Увійти"], button[aria-label="Sign in"], button[aria-label="Увійти"]'
            )).filter(isVisible);

            const signInButtons = Array.from(document.querySelectorAll('a, button')).filter(el => {
                if (!isVisible(el)) return false;
                const t = (el.innerText || '').trim();
                return /^(увійти|sign\s+in|войти)$/i.test(t);
            });

            if (signInAnchors.length > 0 || signInButtons.length > 0) {
                return JSON.stringify({ state: 'unauthenticated', url: location.href });
            }

            const currentRegexes = [
                /поточне\s+використання/i,
                /current\s+usage/i,
                /5-годинн/i,
                /5-hour/i,
                /текущее\s+использование/i
            ];

            const weeklyRegexes = [
                /тижневий\s+ліміт/i,
                /тижнев[еі]\s+використання/i,
                /weekly\s+limit/i,
                /weekly\s+usage/i,
                /недельный\s+лимит/i
            ];

            const matchesAny = (text, regexes) => {
                if (!text) return false;
                return regexes.some(r => r.test(text));
            };

            const extractPercentFromContainer = (container) => {
                if (!container) return null;
                const txt = (container.innerText || '').replace(/\u00A0/g, ' ');

                if (/менше\s*1\s*%|<\s*1\s*%|less\s+than\s+1\s*%/i.test(txt)) {
                    return 0;
                }

                const pctMatch = txt.match(/(\d{1,3})\s*%/);
                if (pctMatch) {
                    const val = parseInt(pctMatch[1], 10);
                    if (!isNaN(val) && val >= 0 && val <= 100) {
                        return val;
                    }
                }

                const bar = container.querySelector('[role="progressbar"]');
                if (bar) {
                    const nowAttr = bar.getAttribute('aria-valuenow');
                    const maxAttr = bar.getAttribute('aria-valuemax');
                    if (nowAttr !== null) {
                        let num = parseFloat(nowAttr);
                        const max = maxAttr ? parseFloat(maxAttr) : 100;
                        if (!isNaN(num)) {
                            if (max === 1 && num <= 1) num = num * 100;
                            return Math.round(Math.max(0, Math.min(100, num)));
                        }
                    }

                    const fill = bar.querySelector('.mdc-linear-progress__primary-bar, [style*="scaleX"], [style*="width"]');
                    if (fill) {
                        const st = fill.getAttribute('style') || '';
                        const scaleMatch = st.match(/scaleX\(\s*([0-9.]+)\s*\)/i);
                        if (scaleMatch) {
                            return Math.round(parseFloat(scaleMatch[1]) * 100);
                        }
                        const widthMatch = st.match(/width:\s*([0-9.]+)%/i);
                        if (widthMatch) {
                            return Math.round(parseFloat(widthMatch[1]));
                        }
                    }
                }

                return null;
            };

            const findMetricByLabels = (targetRegexes, oppositeRegexes) => {
                const all = Array.from(document.querySelectorAll('span, div, p, h1, h2, h3, h4, td, li'));
                const candidates = all.filter(el => {
                    if (!isVisible(el)) return false;
                    const t = (el.innerText || '').trim();
                    return t.length > 0 && t.length < 90 && matchesAny(t, targetRegexes) && !matchesAny(t, oppositeRegexes);
                });

                // Обираємо найбільш точний (листовий) елемент із заголовком шкали
                candidates.sort((a, b) => (a.innerText.length - b.innerText.length));

                for (const labelEl of candidates) {
                    let node = labelEl;
                    let bestSection = labelEl.parentElement;

                    for (let depth = 0; depth < 6 && node && node.parentElement; depth++) {
                        const parent = node.parentElement;
                        const parentText = (parent.innerText || '');

                        // Зупиняємося ПЕРЕД спільним контейнером, який одночасно містить і Поточне, і Тижневий ліміт
                        if (matchesAny(parentText, oppositeRegexes)) {
                            break;
                        }

                        bestSection = parent;
                        const found = extractPercentFromContainer(bestSection);
                        if (found !== null) {
                            return found;
                        }
                        node = parent;
                    }

                    const fallbackVal = extractPercentFromContainer(bestSection);
                    if (fallbackVal !== null) {
                        return fallbackVal;
                    }
                }

                return null;
            };

            let currentVal = findMetricByLabels(currentRegexes, weeklyRegexes);
            let weeklyVal = findMetricByLabels(weeklyRegexes, currentRegexes);

            // Резервний пошук: якщо відкрито вікно "Ліміти використання", але знайдено лише 2 прогрес-бари по порядку
            const bodyText = document.body ? (document.body.innerText || '') : '';
            const hasUsageHeader = /ліміти\s+використання|usage\s+limits|лимиты\s+использования/i.test(bodyText);

            if (hasUsageHeader && (currentVal === null || weeklyVal === null)) {
                const bars = Array.from(document.querySelectorAll('[role="progressbar"]')).filter(isVisible);
                if (bars.length >= 2) {
                    if (currentVal === null) currentVal = extractPercentFromContainer(bars[0].parentElement) ?? extractPercentFromContainer(bars[0]);
                    if (weeklyVal === null) weeklyVal = extractPercentFromContainer(bars[1].parentElement) ?? extractPercentFromContainer(bars[1]);
                }
            }

            if (currentVal !== null || weeklyVal !== null) {
                return JSON.stringify({
                    state: 'extracted',
                    current: currentVal,
                    weekly: weeklyVal,
                    url: location.href,
                    snippet: bodyText.slice(0, 300)
                });
            }

            // Якщо шкали ще не відкриті на екрані — шукаємо пункт меню "Ліміти використання" / "Використання"
            if (allowClickUsage) {
                const menuTargets = Array.from(document.querySelectorAll('a, button, [role="menuitem"], [role="listitem"], mat-list-item, div[tabindex]'))
                    .filter(isVisible);

                let usageItem = menuTargets.find(el => {
                    const t = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).trim();
                    return t.length < 100 && /ліміти\s+використання|usage\s+limits|лимиты\s+использования/i.test(t);
                });

                if (!usageItem) {
                    const popupItems = Array.from(document.querySelectorAll('.cdk-overlay-container [role="menuitem"], .cdk-overlay-container a, .cdk-overlay-container button, [role="menu"] [role="menuitem"]'))
                        .filter(isVisible);
                    usageItem = popupItems.find(el => {
                        const t = (el.innerText || '').trim();
                        return /^використання$|^usage$|^использование$/i.test(t);
                    });
                }

                if (usageItem) {
                    usageItem.click();
                    return JSON.stringify({ state: 'navigating', action: 'clicked_usage', url: location.href });
                }
            }

            // Якщо меню ще закрите — натискаємо кнопку "Налаштування" (шестерня внизу ліворуч)
            if (allowClickSettings) {
                const buttons = Array.from(document.querySelectorAll('button, [role="button"], a')).filter(isVisible);
                const settingsBtn = buttons.find(el => {
                    const label = [
                        el.getAttribute('aria-label') || '',
                        el.getAttribute('mattooltip') || '',
                        el.getAttribute('data-test-id') || '',
                        el.innerText || ''
                    ].join(' ');
                    if (/налаштування|settings|настройки/i.test(label)) return true;
                    const icon = el.querySelector('mat-icon, .google-symbols, .material-icons');
                    if (icon && /^\s*settings(_spark)?\s*$/i.test(icon.innerText || '')) return true;
                    return false;
                });

                if (settingsBtn) {
                    settingsBtn.click();
                    return JSON.stringify({ state: 'navigating', action: 'clicked_settings', url: location.href });
                }
            }

            return JSON.stringify({ state: 'waiting', url: location.href });
        })();
        """;
    }

    public void Dispose()
    {
        _idleTimer.Stop();
        _postEnterCts?.Cancel();
        _postEnterCts?.Dispose();
        _scrapeLock.Dispose();
    }
}
