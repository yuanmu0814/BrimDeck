using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Adapted from upstream #377. Compresses empty dates without losing their positions and fits labels to the available width.
internal sealed class UsageTrendGraph : FrameworkElement
{
    private const double Pad = 3, LabelBand = 17;
    private readonly UsageDateRange _range;
    private readonly IReadOnlyDictionary<int, UsageTrendDay> _days;
    private readonly int _count;
    private readonly Brush _fill;
    private readonly bool _incomplete, _unpriced;
    private readonly long _peakTokens;
    private readonly CostSummary? _peakCost;
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse, StaysOpen = true };
    private bool _cost;
    private int _selected = -1;

    public UsageTrendGraph(UsageDateRange range, IReadOnlyDictionary<int, UsageTrendDay> days, string color, bool incomplete, double height)
    {
        _range = range; _days = days; _fill = UI.Brush(color); _incomplete = incomplete;
        _count = (range.End - range.Start).Days + 1;
        _peakTokens = days.Values.Select(day => day.Tokens).DefaultIfEmpty().Max();
        _peakCost = days.Values.Where(day => day.Cost.HasValue).MaxBy(day => day.Cost.Amount)?.Cost;
        _unpriced = days.Values.Any(day => day.Cost.Unpriced > 0);
        Height = height;
        ClipToBounds = true;
        Focusable = true;
        _tip.PlacementTarget = this;
        Unloaded += (_, _) => ClearDay();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearDay(); };
        AutomationProperties.SetAutomationId(this, "model-usage-trend-graph");
    }

    public string PeakText
    {
        get
        {
            if (_cost && _peakCost is null && _unpriced) return Loc.T("费用峰值未知", "Cost peak unknown");
            bool partial = _incomplete || _cost && _unpriced;
            string amount = _cost ? _peakCost is { } price ? UI.Amount(price) : "$0.00" : UI.Number(_peakTokens);
            return partial ? Loc.T($"已知峰值 {amount}", $"Known peak {amount}") : Loc.T($"单日峰值 {amount}", $"Daily peak {amount}");
        }
    }

    public void SetMetric(bool cost)
    {
        _cost = cost;
        ClearDay();
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenPeer(this, () =>
        Loc.T("用量趋势，", "Usage trend, ") + PeakText + (_selected >= 0 ? "\n" + Describe(_selected) : ""));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double width = ActualWidth - 2 * Pad;
        if (width <= 0) return;
        int day = (int)Math.Floor((e.GetPosition(this).X - Pad) / width * _count);
        ShowDay(Math.Clamp(day, 0, _count - 1), PlacementMode.Mouse);
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); ClearDay(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); ClearDay(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int day = e.Key switch
        {
            Key.Left => _selected <= 0 ? 0 : _selected - 1,
            Key.Right => Math.Min(_count - 1, _selected + 1),
            Key.Home => 0,
            Key.End => _count - 1,
            _ => -1
        };
        if (day < 0) return;
        ShowDay(day, PlacementMode.Bottom);
        e.Handled = true;
    }

    private string Describe(int index)
    {
        string date = Date(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var day = _days.GetValueOrDefault(index);
        string amount;
        if (day is null && _incomplete) amount = Loc.T("暂无已知记录", "No records available");
        else if (!_cost) amount = $"{day?.Tokens ?? 0:N0} Token";
        else if (day is null) amount = "$0.00";
        else
        {
            var price = day.Cost;
            amount = !price.HasValue && price.Unpriced > 0 ? Loc.T("费用未知", "Cost unknown") : UI.Cost(price);
            if (price.IsEstimate) amount += Loc.T("（含估算）", " (includes estimates)");
            if (price.Unpriced > 0) amount += "\n" + Loc.T($"{price.Unpriced} 条记录未计价", Loc.Count(price.Unpriced, "unpriced record", "unpriced records"));
        }
        if (_incomplete) amount += "\n" + Loc.T("所选时段的记录尚未读取完整", "Records for the selected period are incomplete");
        return date + "\n" + amount;
    }

    private void ShowDay(int day, PlacementMode placement)
    {
        if (_selected == day && _tip.IsOpen && _tip.Placement == placement) return;
        _selected = day;
        _tip.Placement = placement;
        _tip.Content = Describe(day);
        _tip.IsOpen = true;
        InvalidateVisual();
    }

    private void ClearDay()
    {
        if (_selected < 0) return;
        _selected = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    private DateTime Date(int index) => _range.Start.AddDays(index);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        double width = ActualWidth - 2 * Pad, bottom = ActualHeight - LabelBand, top = 3;
        if (width <= 0 || bottom <= top) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double slot = width / _count;
        double bar = Math.Min(width, Math.Clamp(slot * .72, 1 / dpi.DpiScaleX, 44));
        double peak = _cost ? (double)(_peakCost?.Amount ?? 0) : _peakTokens;
        double Center(int index) => Pad + (index + .5) * slot;
        var axis = new Pen(UI.Brush(UI.Hairline), 1 / dpi.DpiScaleY);
        var missing = new Pen(UI.Brush(UI.Secondary), 1);
        dc.DrawLine(axis, new Point(Pad, bottom), new Point(Pad + width, bottom));
        if (_selected >= 0)
            dc.DrawRectangle(UI.Brush(UI.FillSelected), null, new Rect(Pad + _selected * slot, top, slot, bottom - top));
        foreach (var (index, day) in _days)
        {
            double value = _cost ? (double)day.Cost.Amount : day.Tokens;
            double height = peak > 0 && value > 0 ? Math.Max(2, (bottom - top) * value / peak) : 0;
            bool unknown = _cost && day.Cost.Unpriced > 0;
            double x = Math.Clamp(Center(index) - bar / 2, Pad, Pad + width - bar);
            if (height > 0)
                dc.DrawRoundedRectangle(_fill, null, new Rect(x, bottom - height, bar, height), 2, 2);
            if (unknown)
                dc.DrawLine(missing, new Point(x, bottom - height - 1), new Point(x + bar, bottom - height - 1));
        }

        var type = new Typeface(UI.PanelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Label(int index) => new(Date(index).ToString("MM-dd", CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, type, 10, UI.Brush(UI.Tertiary), dpi.PixelsPerDip);
        double X(int index, FormattedText text) => Math.Clamp(Center(index) - text.Width / 2, Pad, Pad + Math.Max(0, width - text.Width));
        var last = Label(_count - 1);
        double lastX = X(_count - 1, last), nextX = Pad;
        int intervals = Math.Max(1, (int)(width / (last.Width + 10)) - 1);
        int step = Math.Max(1, (int)Math.Ceiling((_count - 1d) / intervals));
        for (int index = 0; index < _count - 1; index += step)
        {
            var label = Label(index);
            double x = X(index, label);
            if (x < nextX || x + label.Width + 6 > lastX) continue;
            dc.DrawText(label, new Point(x, bottom + 3));
            nextX = x + label.Width + 6;
        }
        dc.DrawText(last, new Point(lastX, bottom + 3));
    }
}
