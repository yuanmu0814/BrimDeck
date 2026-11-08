using BrimDeck.Core;

static class QuotaForecastTests
{
    public static void Run(Action<string, bool> check)
    {
        var opened = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        var metric = new UsageMetric(MetricKind.Percent, "每周额度") { Percent = 20, Window = 10080, ResetAt = opened.AddDays(7) };
        ProviderSnapshot Fresh(DateTimeOffset at) => new(ProviderId.Codex) { LiveQuota = true, QuotaTime = at };
        QuotaForecast? At(double used, double seconds) { var now = opened.AddSeconds(seconds); return QuotaForecast.For(metric with { Percent = used }, Fresh(now), now); }

        // The original Pacer regression vectors, expressed as account usage rather than its ring's remaining value.
        check("Quota forecast: 20% used halfway through the week leaves 60% at reset", At(20, 302400) is { Kind: QuotaForecastKind.RemainingAtReset, Value: 60 });
        check("Quota forecast: an even pace lasts exactly until reset", At(50, 302400) is { Kind: QuotaForecastKind.RemainingAtReset, Value: 0 });
        check("Quota forecast: no consumption keeps the whole allowance", At(0, 302400) is { Kind: QuotaForecastKind.RemainingAtReset, Value: 100 });
        var fast = At(80, 151200);
        check("Quota forecast: 80% used in a quarter-week runs out in 10h30m", fast is { Kind: QuotaForecastKind.RunsOut } && Math.Abs(fast.Value - 37800) < 0.001);
        check("Quota forecast: exhausted quota is reported immediately even in a new window", At(100, 60) is { Kind: QuotaForecastKind.RunsOut, Value: 0 });
        check("Quota forecast: waits during the first 5% of the cycle", At(10, 0)?.Kind == QuotaForecastKind.TooEarly && At(10, 30239)?.Kind == QuotaForecastKind.TooEarly && At(10, 30241)?.Kind != QuotaForecastKind.TooEarly);
        check("Quota forecast: an elapsed reset has no estimate", At(50, 604800) is null && At(50, 604801) is null);
        check("Quota forecast: implausible percentage inputs never produce dates", new double?[] { null, double.NaN, double.PositiveInfinity }.All(value => QuotaForecast.For(metric with { Percent = value }, Fresh(opened), opened) is null));

        var half = opened.AddDays(3.5);
        check("Quota forecast: unknown windows and resets stay unknown", QuotaForecast.For(metric with { Window = null }, Fresh(half), half) is null
            && QuotaForecast.For(metric with { Window = 0 }, Fresh(half), half) is null && QuotaForecast.For(metric with { ResetAt = null }, Fresh(half), half) is null);
        var stale = Fresh(half); stale.IsStale = true;
        check("Quota forecast: stale and failed reads are not extrapolated", QuotaForecast.For(metric, stale, half) is null);
        check("Quota forecast: local-only and unread data are not extrapolated", QuotaForecast.For(metric, new(ProviderId.Codex) { QuotaTime = half }, half) is null
            && QuotaForecast.For(metric, new(ProviderId.Codex) { LiveQuota = true }, half) is null);
        var old = Fresh(half);
        check("Quota forecast: expires after five minutes without a quota update", QuotaForecast.For(metric, old, half.AddMinutes(5)) is not null && QuotaForecast.For(metric, old, half.AddMinutes(5).AddSeconds(1)) is null);
        check("Quota forecast: invalid future capture times are hidden", QuotaForecast.For(metric, Fresh(half.AddMinutes(2)), half) is null);
        check("Quota forecast: display-only metrics, balances and unlimited grants are excluded", QuotaForecast.For(metric with { Kind = MetricKind.Text }, Fresh(half), half) is null
            && QuotaForecast.For(metric with { Kind = MetricKind.Balance, Amount = 20, Total = 100 }, Fresh(half), half) is null
            && QuotaForecast.For(metric with { Unlimited = true }, Fresh(half), half) is null);
        var shortWindow = metric with { Label = "5 小时额度", Window = 300, Percent = 75, ResetAt = opened.AddHours(5) };
        var hour = opened.AddHours(1);
        var claude = QuotaForecast.For(shortWindow, new(ProviderId.Claude) { LiveQuota = true, QuotaTime = hour }, hour);
        check("Quota forecast: Claude's five-hour allowance is forecast independently", claude is { Kind: QuotaForecastKind.RunsOut } && Math.Abs(claude.Value - 1200) < 0.001);
        check("Quota forecast: overdrawn and negative usage are clamped like Pacer", At(120, 302400) is { Kind: QuotaForecastKind.RunsOut, Value: 0 } && At(-5, 302400) is { Kind: QuotaForecastKind.RemainingAtReset, Value: 100 });

        string language = Loc.Language;
        try
        {
            Loc.Use(Loc.Chinese);
            check("Quota forecast: Chinese shows exhaustion, reset remainder and early state", fast!.Text().Contains("10 小时 30 分钟后用完")
                && At(20, 302400)!.Text().Contains("剩余 60%") && At(10, 0)!.Text().Contains("暂不预测"));
            check("Quota forecast: hover help includes an absolute exhaustion time and its basis", fast!.Description(opened.AddSeconds(151200), metric.ResetAt!.Value).Contains("预计用完时间：")
                && fast.Description(opened.AddSeconds(151200), metric.ResetAt.Value).Contains("平均消耗速度"));
            Loc.Use(Loc.English);
            check("Quota forecast: English text and help follow the selected language", fast!.Text().Contains("run out in 10h 30m") && fast.Description(half, metric.ResetAt!.Value).Contains("Expected exhaustion:"));
            check("Quota forecast: sub-minute exhaustion stays meaningful", new QuotaForecast(QuotaForecastKind.RunsOut, 20).Text().Contains("under 1m"));
        }
        finally { Loc.Use(language); }
    }
}
