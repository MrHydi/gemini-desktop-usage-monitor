namespace GeminiUsageMonitor.Models;

public enum HighlightLevel
{
    None = 0,
    Amber75 = 1,
    Red90 = 2
}

public sealed record UsageSnapshot(
    int? CurrentUsagePercent,
    int? WeeklyLimitPercent,
    DateTime Timestamp,
    bool IsAuthenticated,
    string StatusMessage,
    string? RawDebugText = null)
{
    public bool HasValues => CurrentUsagePercent.HasValue || WeeklyLimitPercent.HasValue;

    public static UsageSnapshot Initial() => new(
        CurrentUsagePercent: null,
        WeeklyLimitPercent: null,
        Timestamp: DateTime.Now,
        IsAuthenticated: true,
        StatusMessage: "Очікування першого зчитування лімітів...");
}

public sealed record AlertNotification(
    string Title,
    string Message,
    bool IsCritical);
