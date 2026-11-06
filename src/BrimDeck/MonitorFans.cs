using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// Both physical fans on the monitor page. Ratios stay visible at narrow sizes;
// RPMs are also shown when they fit and always remain available in the tooltip.
internal sealed class FansBlock(MonitorColors colors, double body, double caption, double row) : FrameworkElement
{
    private FanSample? _sample;

    public void Set(Reading<FanSample> reading)
    {
        _sample = reading.OrDefault;
        ToolTip = _sample is { } sample ? FanSpeeds.Details(sample) : Loc.T("风扇：暂无读数", "Fans: no reading");
        AutomationProperties.SetName(this, Loc.T("风扇，", "Fans, ") + ToolTip);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, row);
    protected override AutomationPeer OnCreateAutomationPeer() => new SpokenPeer(this, () => AutomationProperties.GetName(this));

    protected override void OnRender(DrawingContext drawing)
    {
        double gap = 16, width = Math.Max(0, (ActualWidth - gap) / 2), ppd = MonitorDraw.PixelsPerDip(this);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        Entry(0, width, "CPU", _sample?.CpuRpm, colors.Performance);
        Entry(width + gap, width, "GPU", _sample?.GpuRpm, colors.Gpu);

        void Entry(double left, double span, string name, int? rpm, string color)
        {
            double dial = Math.Round(body + 5), center = ActualHeight / 2, baseline = Math.Round(center + body * .36);
            double? ratio = rpm is { } speed ? FanSpeeds.Fraction(speed, _sample!.MaximumRpm) : null;
            MonitorDraw.Dial(drawing, new Point(left + dial / 2, center), dial, 2, 7, ratio, rpm is null ? UI.Tertiary : color, "F", ppd);
            double x = left + dial + 6;
            var label = MonitorDraw.Text(name, caption, UI.Tertiary, ppd);
            MonitorDraw.Baseline(drawing, label, x, baseline); x += label.WidthIncludingTrailingWhitespace + 6;
            var figure = MonitorDraw.Text(ratio is { } share ? MonitorFormat.Percent(share) + "%" : "—", body, UI.Primary, ppd, strong: true);
            MonitorDraw.Baseline(drawing, figure, x, baseline); x += figure.WidthIncludingTrailingWhitespace + 8;
            if (rpm is not { } value) return;
            var revolutions = MonitorDraw.Text(FanSpeeds.Rpm(value), caption, UI.Tertiary, ppd);
            if (x + revolutions.WidthIncludingTrailingWhitespace <= left + span)
                MonitorDraw.Baseline(drawing, revolutions, x, baseline);
        }
    }
}
