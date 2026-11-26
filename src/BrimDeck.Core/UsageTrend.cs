namespace BrimDeck.Core;

public sealed record UsageTrendDay(long Tokens, CostSummary Cost);

public static class UsageTrend
{
    // Based on upstream #377. Store only days with records so long custom ranges do not allocate empty buckets.
    // The keys are offsets from the first local calendar day. Missing keys occupy a zero slot in the graph.
    public static IReadOnlyDictionary<int, UsageTrendDay> Days(IEnumerable<TokenEntry> entries, UsageDateRange range,
        Func<IEnumerable<TokenEntry>, CostSummary> summarize)
    {
        int last = (range.End - range.Start).Days;
        return entries.GroupBy(entry => (entry.Time.LocalDateTime.Date - range.Start).Days)
            .Where(day => day.Key >= 0 && day.Key <= last)
            .ToDictionary(day => day.Key, day => new UsageTrendDay(day.Sum(entry => entry.Total), summarize(day)));
    }
}
