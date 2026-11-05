# Gemini Desktop Usage Limit Monitor (`⚡ 75%+ / 90%+`)

Lightweight native Windows companion utility (**C# / .NET 8 / WPF / WebView2**) for the official **Google Gemini Desktop App for Windows** (`Gemini.exe`, including the `Alt + Space` quick overlay).

It displays a clean, click-through usage pill (`⚡ Current % | 📅 Weekly %`) in the top bar of the Gemini window, highlights the window border with a soft glow when **Current Usage** reaches **75%+** (amber) or **90%+** (red), and sends one-time notifications so you never hit compute caps unexpectedly mid-task.

---

## English

### Why a Companion Overlay?
The official Google Gemini desktop client on Windows is an Electron app with production Electron Fuses enabled (`OnlyLoadAppFromAsar`, disabled `remote-debugging-port` and `NODE_OPTIONS`) and frequent silent auto-updates (`%LOCALAPPDATA%\Google\Gemini\app-*`). Patching `app.asar` breaks integrity and gets wiped on every update.

Instead, **GeminiUsageMonitor** runs zero-intrusion alongside `Gemini.exe`:
- **Pixel-perfect Win32 Click-Through Overlay:** Tracks both the main Gemini window and the `Alt + Space` floating window via `SetWinEventHook` + `DWMWA_EXTENDED_FRAME_BOUNDS` (`PerMonitorV2` DPI-aware). Mouse clicks and scrolling pass straight through (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE`), positioned cleanly to the left of all 4 header controls (`=`, `—`, `□`, `✕`).
- **Dual-Metric Tracking (`⚡ Current Usage` & `📅 Weekly Limit`):**
  - **Top-right translucent pill:** Always visible while Gemini is active (`⚡ X% | 📅 Y%`).
  - **Soft Frame Glow:** Driven **exclusively** by `⚡ Current Usage` — **Amber** at `75%–89%`, **Red** at `90%+`, with a `3%` hysteresis (`< 72%`) to prevent flickering.
  - **Independent One-Time Notifications:** Separate `75%+` and `90%+` triggers for both Current and Weekly limits (Windows System Toast + fallback in-overlay banner if Windows Focus Assist / Do Not Disturb is active).
- **Adaptive Post-`Enter` Exponential Polling (`10s × 2`):**
  - Gemini updates compute usage *after* a response finishes generating.
  - Pressing `Enter` (ignoring `Shift + Enter` newlines) schedules a check after **`10s`**. If percentages haven't changed yet (long reasoning/code generation), it doubles the delay (`10s → 20s → 40s → 80s`, max 4 attempts) and resets back to `10s` as soon as updated stats arrive.
- **Built-in Simulation Mode:** Test `78%` (Amber), `92%` (Red), and Weekly-only alerts directly from the system tray menu without spending real Gemini quota.

### Build & Run
Requirements: Windows 10/11 (Edge WebView2 Runtime is pre-installed on Windows 10/11) and [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
.\publish\GeminiUsageMonitor.exe
```

On first launch, sign in once to your Google account in the WebView2 window and click **"Сховати у фон (Трей)"** (or close the window) — your session is stored locally in `%LOCALAPPDATA%\GeminiUsageMonitor\WebView2Profile`.

---

## Українська

### Опис
Легка фонова трей-утиліта на **C# (.NET 8 + WPF + WebView2)** для офіційного додатку **Google Gemini для Windows** (`Gemini.exe`, включно з оверлеєм `Alt + Space`).

Оскільки офіційний клієнт Gemini приховує відсотки лімітів глибоко в меню *Налаштування → Ліміти використання* і блокує зовнішні порти відладки на рівні Electron Fuses, утиліта працює повністю автономно — не змінює файли `Gemini.exe` і не злітає після автооновлень клієнта.

### Основні можливості
- **Постійна напівпрозора капсула** у верхній панелі активного вікна Gemini (`⚡ Поточне % | 📅 Тижневий %`), розташована ліворуч від усіх 4 кнопок керування вікном (`=`, `—`, `□`, `✕`). Повністю пропускає крізь себе кліки миші (`click-through`).
- **М'яке підсвічування контуру вікна** (реагує **лише на Поточне використання `⚡`**):
  - `75% – 89%` — бурштинове/жовтогаряче світіння;
  - `90%+` — червоне світіння;
  - автоматично гасне при відновленні квоти нижче `72%` (гістерезис `3%`).
- **Незалежні разові сповіщення** на порогах `75%+` та `90%+` для кожної зі шкал (системний Toast Windows + дублюючий банер на оверлеї, якщо увімкнено режим «Не турбувати»).
- **Розумний таймер після `Enter` (`10 с × 2`):**
  - оскільки Gemini оновлює статистику лише після завершення генерації відповіді, перше зчитування відбувається через `10 с` після натискання `Enter` (без `Shift`);
  - якщо дані ще ті самі — інтервал подвоюється (`10 → 20 → 40 → 80 с`, до 4 спроб), і миттєво скидається назад до `10 с`, щойно статистика оновилась.
- **Меню в системному треї:** ручне оновлення, швидкий перегляд сторінки лімітів, вбудований **«🧪 Тест попередження»** (`78%` / `92%`) та автозапуск разом із Windows.

### Швидкий старт
1. Запустіть `Запустити_монітор.bat` або `publish\GeminiUsageMonitor.exe`.
2. При першому старті відкриється вікно входу в Google — авторизуйтесь один раз і сховайте вікно у фон.
3. Відкрийте Gemini (або натисніть `Alt + Space`).

## License
[MIT](./LICENSE)
