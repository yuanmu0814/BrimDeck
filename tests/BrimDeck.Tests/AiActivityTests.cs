using System.Text;
using System.Text.Json;
using BrimDeck.Core;

internal static class AiActivityTests
{
    internal static void Run(Action<string, bool> check)
    {
        var value = AiActivitySnapshot.Parse(Encoding.UTF8.GetBytes("""
            {"protocolVersion":1,"type":"snapshot","sequence":1,"capturedAt":1800000000,"tasks":[],"requests":[],"sources":[],"modules":null,
            "runningCount":2,"waitingCount":0,"totalTokensPerSecond":36.4,"rateIsFresh":true,
            "presentation":{"rows":[],"header":{"activeCount":2,"orbit":{}},"taskCountText":"2"}}
            """));
        check("AI protocol accepts optional module metadata", value.Modules.Count == 0);
        check("AI compact shows aggregate fresh throughput", AiActivity.Compact(value) == "2 · 36 t/s");
        check("AI compact hides stale throughput", AiActivity.Compact(value with { RateIsFresh = false }, true) == "2 running");
        check("AI compact waiting takes priority over throughput", AiActivity.Compact(value with { WaitingCount = 1 }) == "待确认 1");
        check("AI compact idle is quiet", AiActivity.Compact(AiActivitySnapshot.Empty) == "");
        check("AI compact rejects nonsensical throughput", AiActivity.Compact(value with { TotalTokensPerSecond = double.PositiveInfinity }, true) == "2 running");
        var ended = JsonSerializer.Deserialize<AiTaskRow>("""{"Id":"ended","Unread":true,"Failed":true,"Phase":"completed"}""")!;
        var failed = value with { Presentation = value.Presentation! with { Rows = [ended] } };
        check("AI compact failures do not appear as successful completions", AiActivity.Compact(failed, true) == "× 1 failed");
        var completed = failed with { RunningCount = 0, Presentation = failed.Presentation! with { Rows = [ended with { Failed = false }] } };
        check("AI compact retains unread completions", AiActivity.Compact(completed, true) == "✓ 1 done");
        check("AI compact distinguishes interrupted tasks", AiActivity.Compact(completed with { Presentation = completed.Presentation! with { Rows = [ended with { Failed = false, Phase = "interrupted" }] } }, true) == "Ⅱ 1 ended");
        foreach (var invalid in new[] { "{}", "{\"protocolVersion\":2}", "null" })
        {
            bool rejected = false;
            try { AiActivitySnapshot.Parse(Encoding.UTF8.GetBytes(invalid)); } catch (Exception e) when (e is InvalidDataException or JsonException) { rejected = true; }
            check("AI rejects incomplete or incompatible snapshot " + invalid, rejected);
        }
        var settings = new DeckSettings { AiActivityEnabled = false, AiHideTaskTitles = true, AiRetentionMinutes = 120 };
        var copy = settings.Copy();
        check("AI monitoring settings survive copying", !copy.AiActivityEnabled && copy.AiHideTaskTitles && copy.AiRetentionMinutes == 120);
    }
}
