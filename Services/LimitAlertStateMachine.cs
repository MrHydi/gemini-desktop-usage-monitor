using GeminiUsageMonitor.Models;

namespace GeminiUsageMonitor.Services;

/// <summary>
/// Керує станом підсвічування рамки (виключно за Поточним використанням ⚡)
/// та 4 незалежними тригерами разових сповіщень (75%+ та 90%+ для Поточного і Тижневого лімітів)
/// із гістерезисом 3% для захисту від миготіння.
/// </summary>
public sealed class LimitAlertStateMachine
{
    public const int WarningThreshold = 75;
    public const int WarningResetBelow = 72;
    public const int CriticalThreshold = 90;
    public const int CriticalResetBelow = 87;

    private HighlightLevel _currentHighlight = HighlightLevel.None;

    private bool _notifiedCurrent75;
    private bool _notifiedCurrent90;
    private bool _notifiedWeekly75;
    private bool _notifiedWeekly90;

    public HighlightLevel CurrentHighlight => _currentHighlight;

    public (HighlightLevel Highlight, IReadOnlyList<AlertNotification> Notifications) Evaluate(UsageSnapshot snapshot)
    {
        var notifications = new List<AlertNotification>();

        // 1. Підсвітка рамки вікна керується ВИКЛЮЧНО Поточним використанням (⚡)
        if (snapshot.CurrentUsagePercent is int cur)
        {
            _currentHighlight = ComputeNextHighlight(_currentHighlight, cur);

            var curAlert = EvaluateScale(
                value: cur,
                otherValue: snapshot.WeeklyLimitPercent,
                scaleLabel: "Поточне використання ⚡",
                otherLabel: "Тижневий 📅",
                ref _notifiedCurrent75,
                ref _notifiedCurrent90);

            if (curAlert is not null)
            {
                notifications.Add(curAlert);
            }
        }

        // 2. Тижневий ліміт (📅) НЕ змінює підсвітку рамки, але має власні разові сповіщення 75%+ та 90%+
        if (snapshot.WeeklyLimitPercent is int weekly)
        {
            var weeklyAlert = EvaluateScale(
                value: weekly,
                otherValue: snapshot.CurrentUsagePercent,
                scaleLabel: "Тижневий ліміт 📅",
                otherLabel: "Поточне ⚡",
                ref _notifiedWeekly75,
                ref _notifiedWeekly90);

            if (weeklyAlert is not null)
            {
                notifications.Add(weeklyAlert);
            }
        }

        return (_currentHighlight, notifications);
    }

    private static HighlightLevel ComputeNextHighlight(HighlightLevel previous, int currentPercent)
    {
        if (currentPercent >= CriticalThreshold)
        {
            return HighlightLevel.Red90;
        }

        if (previous == HighlightLevel.Red90 && currentPercent >= CriticalResetBelow)
        {
            return HighlightLevel.Red90;
        }

        if (currentPercent >= WarningThreshold)
        {
            return HighlightLevel.Amber75;
        }

        if (previous != HighlightLevel.None && currentPercent >= WarningResetBelow)
        {
            return HighlightLevel.Amber75;
        }

        return HighlightLevel.None;
    }

    private static AlertNotification? EvaluateScale(
        int value,
        int? otherValue,
        string scaleLabel,
        string otherLabel,
        ref bool notified75,
        ref bool notified90)
    {
        // Скидання тригерів після відновлення квоти (гістерезис 3%)
        if (value < CriticalResetBelow)
        {
            notified90 = false;
        }

        if (value < WarningResetBelow)
        {
            notified75 = false;
        }

        string otherSuffix = otherValue.HasValue ? $" ({otherLabel}: {otherValue.Value}%)" : string.Empty;

        if (value >= CriticalThreshold && !notified90)
        {
            notified90 = true;
            notified75 = true; // щоб не дублювати 75%, якщо перескочили одразу за 90%
            return new AlertNotification(
                Title: "Критичний ліміт Gemini (90%+)",
                Message: $"{scaleLabel} досягло {value}%{otherSuffix}.",
                IsCritical: true);
        }

        if (value >= WarningThreshold && !notified75)
        {
            notified75 = true;
            return new AlertNotification(
                Title: "Попередження про ліміт Gemini (75%+)",
                Message: $"{scaleLabel} досягло {value}%{otherSuffix}.",
                IsCritical: false);
        }

        return null;
    }
}
