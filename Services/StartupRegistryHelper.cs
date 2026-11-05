using System.Diagnostics;
using Microsoft.Win32;

namespace GeminiUsageMonitor.Services;

internal static class StartupRegistryHelper
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "GeminiUsageMonitor";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            string? val = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enable)
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppValueName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Ігноруємо помилки доступу до реєстру, якщо політики системи обмежують автозапуск
        }
    }
}
