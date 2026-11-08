using System.Globalization;

namespace BrimDeck.Core;

public enum QuotaForecastKind { TooEarly, RemainingAtReset, RunsOut }

// Port of Codex Pacer's QuotaWindow.projection (MIT, revision 4e397e8c704e).
// Value is a percentage for RemainingAtReset, seconds from now for RunsOut.
// This uses the account's actual allowance, never local token counts or task TPS.
public sealed record QuotaForecast(QuotaForecastKind Kind, double Value = 0)
{
    public bool Warns => Kind == QuotaForecastKind.RunsOut;

    public static QuotaForecast? For(UsageMetric metric, ProviderSnapshot snapshot, DateTimeOffset now)
    {
        // A balance target is not a periodic grant; scripts' display text has no quota semantics.
        // Match Pacer's five-minute freshness limit and hide projections from failed/local-only reads.
        if (metric.Kind != MetricKind.Percent || metric.Unlimited || snapshot.IsStale || !snapshot.LiveQuota
            || snapshot.QuotaTime is not { } captured || now - captured > TimeSpan.FromMinutes(5)
            || captured - now > TimeSpan.FromMinutes(1)
            || metric.Window is not > 0 || metric.ResetAt is not { } reset || reset <= now
            || metric.UsedPercent is not { } percent || !double.IsFinite(percent)) return null;

        double duration = metric.Window.Value * 60.0;
        double time = Math.Clamp((reset - now).TotalSeconds / duration * 100, 0, 100);
        double used = Math.Clamp(percent, 0, 100), remaining = 100 - used;
        if (remaining <= 0) return new(QuotaForecastKind.RunsOut);
        double elapsed = 100 - time;
        if (elapsed < 5) return new(QuotaForecastKind.TooEarly);
        if (used <= 0) return new(QuotaForecastKind.RemainingAtReset, remaining);
        double rate = used / elapsed, left = remaining - rate * time;
        return left >= 0 ? new(QuotaForecastKind.RemainingAtReset, Math.Clamp(left, 0, 100))
            : new(QuotaForecastKind.RunsOut, remaining / rate / 100 * duration);
    }

    public string Text(bool compact = false) => Kind switch
    {
        QuotaForecastKind.TooEarly => compact ? Loc.T("暂不预测", "Too early") : Loc.T("周期刚开始，暂不预测", "Window just started; too early to predict"),
        QuotaForecastKind.RemainingAtReset when Value < 0.5 => compact ? Loc.T("刚好到重置", "Until reset") : Loc.T("预计刚好用到重置", "Expected to last until reset"),
        QuotaForecastKind.RemainingAtReset => compact ? Loc.T($"重置余 {Percent}%", $"{Percent}% at reset") : Loc.T($"预计重置时剩余 {Percent}%", $"Expected {Percent}% left at reset"),
        _ when Value <= 0 => Loc.T("额度已用完", "Quota used up"),
        _ => compact ? Loc.T($"约 {Duration(true)}用完", $"Out in {Duration(true)}") : Loc.T($"预计 {Duration(false)}后用完", $"Expected to run out in {Duration(false)}")
    };

    public string Brief => Kind switch
    {
        QuotaForecastKind.TooEarly => Loc.T("暂不预测", "Too early"),
        QuotaForecastKind.RemainingAtReset => Loc.T("可用至重置", "Until reset"),
        _ when Value <= 0 => Loc.T("已用完", "Used up"),
        _ => Loc.T("预计会用完", "Runs out")
    };

    public string Description(DateTimeOffset now, DateTimeOffset reset)
    {
        string basis = Kind == QuotaForecastKind.TooEarly
            ? Loc.T("额度周期的前 5% 数据不足，稍后自动预测。", "Predictions start after the first 5% of the quota window.")
            : Loc.T("按本轮额度周期内的平均消耗速度推算，结果会随使用变化。", "Based on average quota consumption since this window started; the estimate changes with usage.");
        string moment = Kind == QuotaForecastKind.RunsOut && Value > 0
            ? Loc.T("预计用完时间：", "Expected exhaustion: ") + now.AddSeconds(Value).LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "\n" : "";
        return Text() + "\n" + moment + Loc.T("重置时间：", "Resets: ") + reset.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "\n" + basis;
    }

    private string Percent => Math.Round(Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    private string Duration(bool compact)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Value));
        if (span.TotalMinutes < 1) return Loc.T("不到 1 分钟", "under 1m");
        int days = (int)span.TotalDays, hours = span.Hours, minutes = span.Minutes;
        if (days > 0) return Loc.T(compact ? $"{days}天{hours}时" : $"{days} 天 {hours} 小时", $"{days}d {hours}h");
        if (span.TotalHours >= 1) return Loc.T(compact ? $"{hours}时{minutes}分" : $"{hours} 小时 {minutes} 分钟", $"{hours}h {minutes}m");
        return Loc.T(compact ? $"{minutes}分" : $"{minutes} 分钟", $"{minutes}m");
    }
}
