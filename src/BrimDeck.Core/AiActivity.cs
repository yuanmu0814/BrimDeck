using System.Globalization;
using System.Text.Json;

namespace BrimDeck.Core;

// Protocol 1 of the MIT-licensed Codex Pacer engine. Lifecycle, task grouping and
// rate freshness come from its shared Core; the host only presents the snapshot.
public sealed record AiActivitySnapshot
{
    public int ProtocolVersion { get; init; }
    public string Type { get; init; } = "";
    public ulong Sequence { get; init; }
    public double CapturedAt { get; init; }
    public IReadOnlyList<AiTask> Tasks { get; init; } = [];
    public IReadOnlyList<AiAttention> Requests { get; init; } = [];
    public IReadOnlyList<AiSource> Sources { get; init; } = [];
    public IReadOnlyList<AiModule> Modules { get; init; } = [];
    public int RunningCount { get; init; }
    public int WaitingCount { get; init; }
    public double? TotalTokensPerSecond { get; init; }
    public bool RateIsFresh { get; init; }
    public AiPresentation? Presentation { get; init; }
    public AiWallet? Wallet { get; init; }
    public static AiActivitySnapshot Empty { get; } = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static AiActivitySnapshot Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > 1024 * 1024) throw new InvalidDataException("Activity snapshot exceeds the protocol limit.");
        var value = JsonSerializer.Deserialize<AiActivitySnapshot>(frame, Json) ?? throw new InvalidDataException("Empty activity snapshot.");
        if (value.ProtocolVersion != 1 || value.Type != "snapshot" || value.Sequence == 0 || !double.IsFinite(value.CapturedAt)
            || value.Tasks is null || value.Requests is null || value.Sources is null
            || value.Tasks.Count > 1024 || value.Requests.Count > 1024 || value.Presentation?.Rows is null)
            throw new InvalidDataException("Incompatible activity snapshot.");
        return value with { Modules = value.Modules ?? [] };
    }
}

public sealed record AiTask(string Id, string Provider, string? SessionId, string? TurnId, string? ParentThreadId,
    string Title, string Source, string? SourceId, string Phase, string Stage, string StageLabel,
    string? Model, string? Effort, double? TokensPerSecond, double? RateReportedAt, bool RateIsFresh,
    bool RateIsEstimated, double? FirstTokenLatency, double? StartedAt, bool Unread, bool Failed,
    double? TurnCost, double? TurnPeakCost);
public sealed record AiAttention(string Id, string Provider, string ActivityId, string Kind, string Label, double DetectedAt);
public sealed record AiSource(string Id, string Provider, bool Available, string Transport, string? Reason,
    string? SourceName, string? State, string? StateLabel, int? Sessions);
public sealed record AiModule(string Provider, string Mode, bool? Installed, bool Enabled);
public sealed record AiPresentation(IReadOnlyList<AiTaskRow> Rows, AiHeader Header, string? TaskCountText);
public sealed record AiHeader(int ActiveCount, string? CoreSymbol, IReadOnlyDictionary<string, int> Orbit,
    string? Attention, string? Ending, string? EndingProvider, bool EndingIsCore);
public sealed record AiTaskRow(string Id, string Provider, string Title, string Status, string? Source, string? SourceLabel,
    string? SourceKind, string Phase, string Symbol, string? Attention, string Model, string? Effort,
    double? TokensPerSecond, bool RateIsFresh, string? RatePlaceholder, string? TurnTime, int Subagents,
    bool Unread, bool Failed, bool Dimmed, string? Cost, bool PeakCost, string? RateText, string? SubagentText);
public sealed record AiWallet(string State, double? Balance, double Today, double Elsewhere, double? AverageDaily,
    double? RunwayDays, AiWalletSolo? Solo);
public sealed record AiWalletSolo(string TodayLabel, string TodayText, string TodayHelp, string? PeakUntil,
    AiWalletWeek? Week, IReadOnlyList<AiWalletBudget>? Budgets);
public sealed record AiWalletWeek(string Title, IReadOnlyList<AiWalletDay> Days, string AccessibilityLabel);
public sealed record AiWalletDay(double Timestamp, string Label, bool Today, double WithinFraction, double OverFraction, string Help);
public sealed record AiWalletBudget(string Kind, string Label, double Fraction, string PaceText, string Whole,
    string Cents, string Countdown, string ResetText, string Help, string SpentText);

public static class AiActivity
{
    public static readonly string[] Providers = ["codex", "claude", "deepseek"];
    public static string Name(string provider) => provider switch { "codex" => "Codex", "claude" => "Claude", "deepseek" => "DeepSeek", _ => provider };
    public static int Unread(AiActivitySnapshot value) => value.Presentation?.Rows.Count(row => row.Unread) ?? 0;
    public static int Failed(AiActivitySnapshot value) => value.Presentation?.Rows.Count(row => row.Unread && row.Failed) ?? 0;
    public static string Compact(AiActivitySnapshot value, bool english = false)
    {
        if (value.WaitingCount > 0 || value.Requests.Count > 0) return english ? $"! {Math.Max(value.WaitingCount, value.Requests.Count)}" : $"待确认 {Math.Max(value.WaitingCount, value.Requests.Count)}";
        if (Failed(value) is > 0 and var failed) return english ? $"× {failed} failed" : $"× {failed} 失败";
        if (value.RunningCount > 0) return value.RateIsFresh && value.TotalTokensPerSecond is > 0 and < 1_000_000
            ? $"{value.RunningCount} · {value.TotalTokensPerSecond.Value.ToString("0", CultureInfo.InvariantCulture)} t/s" : english ? $"{value.RunningCount} running" : $"{value.RunningCount} 进行中";
        int unread = Unread(value);
        if (unread > 0 && value.Presentation?.Rows.Any(row => row.Unread && row.Phase == "interrupted") == true)
            return english ? $"Ⅱ {unread} ended" : $"Ⅱ {unread} 已结束";
        return unread > 0 ? english ? $"✓ {unread} done" : $"✓ {unread} 完成" : "";
    }
    public static string Duration(AiTask? task, double now, string? fallback)
    {
        if (task?.Phase == "running" && task.StartedAt is > 0 && double.IsFinite(now) && now >= task.StartedAt)
        {
            long seconds = (long)Math.Min(7 * 86400, now - task.StartedAt.Value);
            return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
        }
        return fallback ?? "—";
    }
}
