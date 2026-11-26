using BrimDeck.Core;

internal static class UsageTrendTests
{
    internal static void Run(Action<string, bool> check)
    {
        var start = new DateTime(2026, 11, 8);
        var end = start.AddDays(2);
        var range = new UsageDateRange(start, end);
        TokenEntry Entry(DateTime day, int hour, long tokens, decimal? cost) =>
            new("same-key", new DateTimeOffset(day.AddHours(hour)).ToUniversalTime(), "model", tokens, 0, 0, 0, 0, cost);
        var entries = new[]
        {
            Entry(start, 0, 100, 1.25m),
            Entry(start, 23, 50, null),
            Entry(end, 23, 200, 0m),
            Entry(start.AddDays(-1), 23, 999, 9m),
            Entry(end.AddDays(1), 0, 999, 9m)
        };
        CostSummary Summarize(IEnumerable<TokenEntry> records)
        {
            var known = records.Where(entry => entry.ReportedCostUsd is not null).ToList();
            return new(known.Sum(entry => entry.ReportedCostUsd!.Value), known.Count, 0, records.Count() - known.Count);
        }
        var trend = UsageTrend.Days(entries, range, Summarize);
        check("Usage trend groups by local calendar day, including both limits",
            trend.Count == 2 && trend[0].Tokens == 150 && trend[2].Tokens == 200);
        check("Usage trend reserves empty dates without reporting activity", !trend.ContainsKey(1));
        check("Usage trend totals match the model range filter",
            trend.Values.Sum(day => day.Tokens) == entries.Where(entry => range.Contains(entry.Time)).Sum(entry => entry.Total));
        check("Usage trend preserves partial costs and reported zero costs",
            trend[0].Cost is { Amount: 1.25m, Reported: 1, Unpriced: 1 } &&
            trend[2].Cost is { Amount: 0m, HasValue: true, Unpriced: 0 });

        var longRange = new UsageDateRange(DateTime.MinValue, end);
        int summarized = 0;
        var sparse = UsageTrend.Days([entries[2]], longRange, records => { summarized++; return Summarize(records); });
        check("A long custom range only summarizes dates with usage", sparse.Count == 1 && summarized == 1 &&
            sparse[(end - longRange.Start).Days].Tokens == 200);
    }
}
