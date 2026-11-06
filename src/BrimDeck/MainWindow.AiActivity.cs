using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    private bool _aiTasksPage;
    private string? _aiProvider;
    private TextBlock? _aiTabCount;
    private TextBlock? _aiSummary;
    private FrameworkElement? _activityBadge;
    private AiStatusIcon? _compactActivityIcon;
    private bool _activityCompactActive;
    private ScrollViewer? _aiScroll;
    private double _aiScrollOffset;
    private string _activityCompactText = "";
    private double UsagePanelHeight => Math.Max(100, ContentCanvas.Height - (!_details && Settings.UsagePage ? 30 : 0));

    internal void ActivityChanged()
    {
        if (_aiTabCount is not null) _aiTabCount.Text = ActivityTabText;
        if (_aiSummary is not null) _aiSummary.Text = AiActivity.Compact(_app.Activity, Loc.IsEnglish);
        if (_activityBadge is not null) _activityBadge.ToolTip = ActivityHelp;
        string compact = AiActivity.Compact(_app.Activity, Loc.Language == "en-US");
        bool active = HasActiveActivity;
        if (compact != _activityCompactText || active != _activityCompactActive)
        { _activityCompactText = compact; _activityCompactActive = active; if (!_expanded) RenderCompact(); }
        else if (_compactActivityIcon is not null) UpdateActivityIcon(_compactActivityIcon);
        if (_expanded && IsUsagePage && _aiTasksPage) RenderUsage();
    }

    private string ActivityTabText
    {
        get
        {
        int count = _app.Activity.Presentation?.Rows.Count ?? 0;
        return Loc.T("实时任务", "Live tasks") + (count > 0 ? $" · {count}" : "");
        }
    }

    private void AddActivityNavigation()
    {
        if (_details || !Settings.UsagePage) return;
        var body = new Grid();
        foreach (var child in UsageContent.Children.Cast<UIElement>().ToArray())
        { UsageContent.Children.Remove(child); body.Children.Add(child); }
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        host.RowDefinitions.Add(new RowDefinition());
        var bar = new DockPanel { Margin = new Thickness(16, 0, 16, 5), LastChildFill = false };
        var more = ActivityButton("···", Loc.T("实时任务设置", "Live task settings"), _app.OpenActivitySettings);
        DockPanel.SetDock(more, System.Windows.Controls.Dock.Right); bar.Children.Add(more);
        _aiSummary = UI.Fixed(AiActivity.Compact(_app.Activity, Loc.IsEnglish), 11, 22, UI.Secondary);
        _aiSummary.Margin = new Thickness(8, 0, 8, 0); _aiSummary.TextTrimming = TextTrimming.CharacterEllipsis;
        _aiSummary.MaxWidth = Math.Max(0, ContentCanvas.Width - 290);
        DockPanel.SetDock(_aiSummary, System.Windows.Controls.Dock.Right); bar.Children.Add(_aiSummary);
        var options = new List<SegmentItem>
        {
            new(Loc.T("用量", "Usage"), Loc.T("额度与费用统计", "Quotas and usage costs"), !_aiTasksPage, () => SelectActivityPage(false)),
            new(ActivityTabText, Loc.T("Codex、Claude、DeepSeek 实时任务", "Live Codex, Claude and DeepSeek tasks"), _aiTasksPage, () => SelectActivityPage(true))
        };
        var tabs = Segmented(options, 11, 22, 10, true);
        if (tabs.Child is StackPanel strip && strip.Children.Count > 1 && strip.Children[1] is Button button && button.Content is TextBlock label) _aiTabCount = label;
        bar.Children.Add(tabs);
        host.Children.Add(bar); Grid.SetRow(body, 1); host.Children.Add(body); UsageContent.Children.Add(host);
    }

    private void SelectActivityPage(bool tasks)
    {
        if (_aiScroll is { IsLoaded: true }) _aiScrollOffset = _aiScroll.VerticalOffset;
        _aiTasksPage = tasks; _usageBuilt = null; _usageHandover = Handover.None; RenderUsage();
    }

    private static Button ActivityButton(string text, string name, Action click)
    {
        var button = new Button { Content = text, FontFamily = UI.PanelFont, FontSize = 11,
            Style = (Style)Application.Current.FindResource("SegmentButton"), Padding = new Thickness(7, 1, 7, 1), ToolTip = name };
        AutomationProperties.SetName(button, name); button.Click += (_, _) => click(); return button;
    }

    private void RenderActivityPage()
    {
        if (_aiScroll is { IsLoaded: true }) _aiScrollOffset = _aiScroll.VerticalOffset;
        _usageForm = "activity"; _usageBuilt = null;
        UsageContent.Margin = new Thickness(6, 0, 6, 8);
        ApplyHeader(UnfittedHeader(ContentCanvas.Height));
        ClockLabel.Visibility = Visibility.Visible; ModelToolbar.Visibility = Visibility.Collapsed;
        var root = new Grid { Margin = new Thickness(16, 2, 16, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var clear = ActivityButton(ContentCanvas.Width < 480 ? "✓" : Loc.T("全部已读", "Mark all read"), _app.ActivityActionMessage ?? Loc.T("将已完成的任务标记为已读", "Mark completed tasks as read"), async () => await _app.ActivityCommandAsync("markAllRead", new { snapshotSequence = _app.Activity.Sequence }));
        clear.IsEnabled = AiActivity.Unread(_app.Activity) > 0;
        DockPanel.SetDock(clear, System.Windows.Controls.Dock.Right); header.Children.Add(clear);
        var providers = new StackPanel { Orientation = Orientation.Horizontal };
        void Filter(string? provider, string label)
        {
            var button = ActivityButton(label, label, () => { _aiProvider = provider; _aiScroll = null; _aiScrollOffset = 0; RenderUsage(); });
            button.Tag = _aiProvider == provider ? "Selected" : null;
            button.Margin = new Thickness(0, 0, 3, 0); providers.Children.Add(button);
        }
        Filter(null, Loc.T("全部", "All"));
        foreach (string provider in AiActivity.Providers)
        {
            int count = _app.Activity.Presentation?.Rows.Count(row => row.Provider == provider) ?? 0;
            Filter(provider, AiActivity.Name(provider) + (count > 0 ? $" {count}" : ""));
        }
        header.Children.Add(providers); root.Children.Add(header);
        var rows = new StackPanel();
        var projected = _app.Activity.Presentation?.Rows ?? [];
        foreach (var row in projected.Where(row => _aiProvider is null || row.Provider == _aiProvider)) rows.Children.Add(ActivityRow(row));
        if ((_aiProvider is null || _aiProvider == "deepseek") && _app.Activity.Wallet is { } wallet && (wallet.Today > 0 || wallet.Solo?.Week?.Days.Any(day => day.WithinFraction > 0 || day.OverFraction > 0) == true))
            rows.Children.Add(ActivityWallet(wallet));
        if (rows.Children.Count == 0)
        {
            string title = _app.ActivityState switch
            {
                "starting" => Loc.T("正在连接任务引擎…", "Connecting activity engine…"),
                "reconnecting" => Loc.T("连接中断，正在自动重连…", "Connection lost. Reconnecting automatically…"),
                "paused" => Loc.T("任务监测已暂停", "Task monitoring is paused"),
                "missing" => Loc.T("此构建尚未包含任务引擎", "The activity engine is not included in this build"),
                "stopped" => Loc.T("任务引擎已断开", "Activity engine disconnected"),
                _ => Loc.T("暂无实时任务", "No live tasks")
            };
            var empty = new StackPanel { Margin = new Thickness(10, 20, 10, 8), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(UI.Fixed(title, 14, 24, UI.Primary));
            var help = UI.Fixed(_app.ActivityState switch
            {
                "missing" => Loc.T("请使用包含 Pacer 引擎的完整安装包。", "Use a complete package that includes the Pacer engine."),
                "reconnecting" => Loc.T("连接恢复后会继续监测现有会话。", "Monitoring existing conversations will resume when connected."),
                _ => Loc.T("启动 Codex、Claude Code 或 DeepSeek Harness 后自动显示。", "Tasks appear when you use Codex, Claude Code or DeepSeek Harness.")
            }, 11, 20, UI.Secondary);
            help.TextWrapping = TextWrapping.Wrap; help.MaxWidth = Math.Max(100, ContentCanvas.Width - 100); empty.Children.Add(help);
            if (_app.ActivityState is "stopped" or "missing") empty.Children.Add(ActivityButton(Loc.T("重试", "Retry"), Loc.T("重新连接任务引擎", "Reconnect activity engine"), _app.RetryActivity));
            rows.Children.Add(empty);
        }
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        _aiScroll = scroll;
        double offset = _aiScrollOffset;
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        Grid.SetRow(scroll, 1); root.Children.Add(scroll); UsageContent.Children.Add(root);
    }

    private FrameworkElement ActivityRow(AiTaskRow row)
    {
        string color = row.Failed ? PanelTheme.AppCritical(new AppEntry()) : row.Attention is not null ? PanelTheme.AppWarning(new AppEntry()) : ActivityColor(row.Provider);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        var title = UI.Fixed(AiActivity.Name(row.Provider) + "  ·  " + row.Title, 12, 20, UI.Primary, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = title.Text; text.Children.Add(title);
        string? source = string.IsNullOrWhiteSpace(row.SourceLabel) ? row.Source : row.SourceLabel;
        string details = string.Join(" · ", new[] { row.Model, row.Effort, source, row.Subagents > 0 ? row.SubagentText ?? $"+{row.Subagents}" : null }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var detail = UI.Fixed(details, 10.5, 18, UI.Secondary); detail.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(detail);
        var stateLine = new DockPanel { LastChildFill = true };
        var symbol = new AiStatusIcon(16) { Margin = new Thickness(0, 0, 5, 0) };
        symbol.Update(row.Symbol, row.Phase == "running" ? new Dictionary<string, int> { [row.Provider] = 1 } : new Dictionary<string, int>(), UI.Brush(color), Settings.AiStatusAnimations && SystemParameters.ClientAreaAnimation);
        stateLine.Children.Add(symbol);
        var state = UI.Fixed(row.Status, 11, 18, color); state.TextTrimming = TextTrimming.CharacterEllipsis; stateLine.Children.Add(state); text.Children.Add(stateLine);
        var stats = new StackPanel { MinWidth = 85, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        string rate = row.RateIsFresh && row.TokensPerSecond is > 0 and < 1_000_000 ? (row.RateText ?? $"{row.TokensPerSecond:0.#}") + " t/s" : row.RatePlaceholder ?? "— t/s";
        stats.Children.Add(UI.Fixed(rate, 12, 20, UI.Primary, FontWeights.SemiBold));
        var task = _app.Activity.Tasks.FirstOrDefault(item => item.Id == row.Id);
        string duration = row.TurnTime ?? AiActivity.Duration(task, _app.Activity.CapturedAt, null);
        var time = UI.Fixed(duration, 11, 18, UI.Secondary); time.ToolTip = Loc.T("本轮运行时长", "Turn duration"); stats.Children.Add(time);
        if (row.Cost is { Length: > 0 }) stats.Children.Add(UI.Fixed(row.Cost, 10.5, 18, UI.Secondary));
        else if (task?.FirstTokenLatency is >= 0 and < 3600) stats.Children.Add(UI.Fixed($"TTFT {task.FirstTokenLatency:0.0}s", 10, 18, UI.Tertiary));
        if (row.Unread)
        {
            var read = ActivityButton(Loc.T("已读", "Read"), Loc.T("标记此任务为已读", "Mark this task as read"), async () => await _app.ActivityCommandAsync("markRead", new { activityID = row.Id, snapshotSequence = _app.Activity.Sequence }));
            stats.Children.Add(read);
        }
        grid.Children.Add(text); Grid.SetColumn(stats, 1); grid.Children.Add(stats);
        var card = new Border { Child = grid, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(card, $"{AiActivity.Name(row.Provider)} {row.Title}, {row.Status}, {rate}, {duration}");
        return card;
    }

    private FrameworkElement ActivityWallet(AiWallet wallet)
    {
        var panel = new StackPanel();
        panel.Children.Add(UI.Fixed("DeepSeek · " + Loc.T("本地会话费用", "Local session costs"), 12, 22, UI.Primary, FontWeights.SemiBold));
        panel.Children.Add(UI.Fixed(wallet.Solo?.TodayText ?? Loc.T($"今日 ¥{wallet.Today:0.00}", $"Today ¥{wallet.Today:0.00}"), 12, 22, UI.Secondary));
        if (wallet.Solo?.Week is { } week)
        {
            var bars = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            foreach (var day in week.Days)
            {
                var dayPanel = new StackPanel { Width = 38, Margin = new Thickness(0, 0, 4, 0), ToolTip = day.Help };
                var bar = new Grid { Height = 30 };
                bar.Children.Add(new Border { Width = 16, Height = Math.Clamp((day.WithinFraction + day.OverFraction) * 30, 2, 30), VerticalAlignment = VerticalAlignment.Bottom,
                    Background = UI.Brush(day.Today ? ActivityColor("deepseek") : UI.Track), CornerRadius = new CornerRadius(3) });
                dayPanel.Children.Add(bar); dayPanel.Children.Add(UI.Fixed(day.Label, 10, 18, UI.Secondary)); bars.Children.Add(dayPanel);
            }
            panel.Children.Add(bars);
        }
        return new Border { Child = panel, Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 2, 0, 6) };
    }

    private static string ActivityColor(string provider) => UI.ColorFor(provider switch { "claude" => ProviderId.Claude, "deepseek" => ProviderId.DeepSeek, _ => ProviderId.Codex });

    private bool HasActiveActivity => Settings.AiActivityEnabled && (_app.Activity.RunningCount > 0 || _app.Activity.WaitingCount > 0 || _app.Activity.Requests.Count > 0);

    private AiStatusIcon? CompactActivityIcon(double scale)
    {
        if (!Settings.ShowsSummary(EffectiveStyle) || !HasActiveActivity) return null;
        var icon = new AiStatusIcon(Math.Round(16 * scale));
        UpdateActivityIcon(icon); _compactActivityIcon = icon;
        return icon;
    }

    private void UpdateActivityIcon(AiStatusIcon icon)
    {
        var value = _app.Activity;
        var header = value.Presentation?.Header;
        var rows = value.Presentation?.Rows ?? [];
        bool attention = value.WaitingCount > 0 || value.Requests.Count > 0;
        // Multiple tasks keep their count in the adjacent text; show the most
        // recent active stage here instead of replacing the glyph with a count.
        string symbol = attention ? rows.FirstOrDefault(row => row.Attention is not null || row.Phase == "waitingForInput")?.Symbol ?? "hand.raised"
            : AiActivity.RunningSymbol(value);
        var orbit = header?.Orbit ?? value.Tasks.Where(t => t.Phase == "running").GroupBy(t => t.Provider).ToDictionary(g => g.Key, g => g.Count());
        icon.Update(symbol, orbit, UI.Brush(attention ? PanelTheme.AppWarning(new AppEntry()) : UI.Primary), Settings.AiStatusAnimations && SystemParameters.ClientAreaAnimation);
        icon.ToolTip = ActivityHelp;
        AutomationProperties.SetName(icon, Loc.T("AI 任务状态：", "AI task status: ") + symbol);
    }

    private FrameworkElement? CompactActivity(double scale, bool concise = false)
    {
        _activityBadge = null;
        if (!Settings.ShowsSummary(EffectiveStyle) || !Settings.AiActivityEnabled) return null;
        string text = AiActivity.Compact(_app.Activity, Loc.Language == "en-US");
        if (text.Length == 0) return null;
        if (concise) text = _app.Activity.WaitingCount > 0 || _app.Activity.Requests.Count > 0 ? $"! {Math.Max(_app.Activity.WaitingCount, _app.Activity.Requests.Count)}"
            : AiActivity.Failed(_app.Activity) > 0 ? $"× {AiActivity.Failed(_app.Activity)}" : _app.Activity.RunningCount > 0 ? $"{_app.Activity.RunningCount}"
            : $"✓ {AiActivity.Unread(_app.Activity)}";
        string color = AiActivity.Failed(_app.Activity) > 0 ? PanelTheme.AppCritical(new AppEntry())
            : _app.Activity.WaitingCount > 0 || _app.Activity.Requests.Count > 0 ? PanelTheme.AppWarning(new AppEntry()) : UI.Accent;
        var label = UI.Fixed(text, 10 * scale, 16 * scale, color, FontWeights.SemiBold);
        label.TextTrimming = TextTrimming.None;
        var content = new Border { Child = label };
        // Measure in the window's DPI, typography and layout-rounding context.
        // Measuring a detached label can reserve less than its attached text needs.
        CompactRings.Children.Add(content);
        try
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            content.Width = DevicePixels.Up(content.DesiredSize.Width, dpi);
            content.Height = DevicePixels.Up(content.DesiredSize.Height, dpi);
        }
        finally { CompactRings.Children.Remove(content); }
        content.ToolTip = ActivityHelp; _activityBadge = content;
        AutomationProperties.SetName(content, Loc.T("AI 任务：", "AI tasks: ") + text);
        return content;
    }
    private string ActivityHelp => string.Join("\n", (_app.Activity.Presentation?.Rows ?? []).Take(8).Select(row => $"{AiActivity.Name(row.Provider)} · {row.Title} · {row.Status}"));
}
