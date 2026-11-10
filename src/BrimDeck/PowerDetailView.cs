using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// A nested monitor page. Its nodes are regular accessible controls; only the ribbons and history draw directly.
// New readings update the same nodes. Hidden pages stop their animation and use the monitor's existing demand/lifetime.
internal sealed class PowerDetailView : Grid
{
    public const double MinimumHeight = 260;
    public Button BackButton { get; }
    private readonly MonitorColors _colors;
    private readonly bool _temperatures, _fans;
    private readonly PowerDetailNode _ac, _battery, _machine, _cpu, _gpu, _other;
    private readonly TextBlock _origin, _status, _source, _batteryInfo, _summary, _fanValue, _cpuHeat, _gpuHeat;
    private readonly PowerDetailCurve _curve;
    private readonly PowerFlowRibbons _ribbons;

    public PowerDetailView(MonitorColors colors, Action back, bool temperatures, bool fans)
    {
        _colors = colors; _temperatures = temperatures; _fans = fans;
        MinHeight = MinimumHeight;
        AutomationProperties.SetAutomationId(this, "monitor-power-details");
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            RowDefinitions.Add(new RowDefinition { Height = height });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        BackButton = new Button
        {
            Content = Loc.T("‹ 返回监控", "‹ Monitor"), FontFamily = UI.PanelFont, FontSize = 12,
            Width = double.NaN,
            Padding = new Thickness(7, 0, 7, 0), Height = 26, VerticalAlignment = VerticalAlignment.Center,
            Style = Application.Current.TryFindResource("PanelSourceButton") as Style
        };
        AutomationProperties.SetAutomationId(BackButton, "power-details-back");
        BackButton.Click += (_, _) => back(); head.Children.Add(BackButton);
        var title = Label(Loc.T("功耗详情", "Power details"), 16, UI.Primary, FontWeights.SemiBold);
        title.Margin = new Thickness(12, 0, 6, 0); SetColumn(title, 1); head.Children.Add(title);
        _origin = Label("", 11, UI.Secondary); SetColumn(_origin, 2); head.Children.Add(_origin); Children.Add(head);

        var diagram = new Grid { MinHeight = 134, Margin = new Thickness(0, 0, 0, 6) };
        foreach (double share in new[] { 1.15, .45, 1.4, .45, 1.15 }) diagram.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        _ac = new("ac", Loc.T("外接电源", "AC power"), "\uE945", colors.Performance);
        _battery = new("battery", Loc.T("电池", "Battery"), "\uE850", colors.Efficiency);
        _machine = new("machine", Loc.T("整机功率", "System power"), "\uE7F4", colors.Performance, large: true);
        _cpu = new("cpu", "CPU", "\uE950", colors.Performance);
        _gpu = new("gpu", "GPU", "\uE7F4", colors.Gpu);
        _other = new("other", Loc.T("其他硬件", "Other hardware"), "\uE713", UI.Secondary);
        _ribbons = new PowerFlowRibbons(diagram, _ac, _battery, _machine, _cpu, _gpu, _other, colors.Efficiency);
        SetColumnSpan(_ribbons, 5); diagram.Children.Add(_ribbons);
        var supplies = new Grid(); supplies.RowDefinitions.Add(new()); supplies.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); supplies.RowDefinitions.Add(new());
        supplies.Children.Add(_ac); SetRow(_battery, 2); supplies.Children.Add(_battery); diagram.Children.Add(supplies);
        SetColumn(_machine, 2); _machine.VerticalAlignment = VerticalAlignment.Center; diagram.Children.Add(_machine);
        var parts = new Grid();
        for (int i = 0; i < 5; i++) parts.RowDefinitions.Add(new RowDefinition { Height = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(5) });
        parts.Children.Add(_cpu); SetRow(_gpu, 2); parts.Children.Add(_gpu); SetRow(_other, 4); parts.Children.Add(_other); SetColumn(parts, 4); diagram.Children.Add(parts);
        _curve = new PowerDetailCurve(PowerStyle.For(colors)) { Height = 22, Margin = new Thickness(0, 1, 0, 0) };
        _summary = Label("", 10, UI.Tertiary); _summary.HorizontalAlignment = HorizontalAlignment.Center;
        _machine.Body.Children.Add(_curve); _machine.Body.Children.Add(_summary);
        SetRow(diagram, 1); Children.Add(diagram);

        _status = Label("", 11, UI.Secondary); _status.TextWrapping = TextWrapping.Wrap; _status.TextTrimming = TextTrimming.None;
        _status.Margin = new Thickness(0, 0, 0, 6); SetRow(_status, 2); Children.Add(_status);
        var vitals = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        foreach (var share in new[] { 1.35, 1.0, 1.0 }) vitals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        _fanValue = Vital(vitals, 0, Loc.T("风扇", "Fans"));
        _cpuHeat = Vital(vitals, 1, Loc.T("CPU 温度", "CPU temp")); _gpuHeat = Vital(vitals, 2, Loc.T("GPU 温度", "GPU temp"));
        SetRow(vitals, 3); Children.Add(vitals);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new());
        _source = Label("", 10, UI.Tertiary); _source.TextWrapping = TextWrapping.Wrap; _source.TextTrimming = TextTrimming.None;
        _batteryInfo = Label("", 10, UI.Tertiary); _batteryInfo.TextAlignment = TextAlignment.Right; _batteryInfo.TextWrapping = TextWrapping.Wrap; _batteryInfo.TextTrimming = TextTrimming.None;
        _source.Margin = new Thickness(0, 0, 8, 0); SetColumn(_batteryInfo, 1); footer.Children.Add(_source); footer.Children.Add(_batteryInfo); SetRow(footer, 4); Children.Add(footer);
        IsVisibleChanged += (_, _) => { if (!IsVisible) SetActive(false); };
        Unloaded += (_, _) => SetActive(false);
    }

    private static TextBlock Label(string text, double size, string color, FontWeight? weight = null)
        => UI.Line(text, size, color, weight);

    private static TextBlock Vital(Grid owner, int column, string name)
    {
        var body = new StackPanel(); body.Children.Add(Label(name, 10, UI.Tertiary));
        var value = Label("—", 12, UI.Primary, FontWeights.Medium); body.Children.Add(value);
        var card = new Border { Background = UI.Brush(UI.Fill), CornerRadius = new CornerRadius(PanelTheme.Corner(8)), Padding = new Thickness(8, 4, 6, 4),
            Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0), Child = body };
        SetColumn(card, column); owner.Children.Add(card); return value;
    }

    private static string Watts(double? value) => value is { } watts ? PowerFormat.Watts(watts) + " W" : "—";
    private static string Percent(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "%";

    public void Update(MonitorSnapshot snapshot, bool? mains)
    {
        var flow = PowerFlow.From(snapshot.Power, snapshot.Battery);
        var cell = flow.Battery;
        _origin.Text = flow.Total is null ? Loc.T("暂无读数", "No reading") : flow.Estimated ? Loc.T("组件估算", "Component estimate") : Loc.T("实测", "Measured");
        _machine.NameText.Text = flow.Estimated ? Loc.T("已测组件合计", "Measured parts") : Loc.T("整机功率", "System power");
        _machine.Value.Text = Watts(flow.Total);
        _machine.ToolTip = PowerText.Spoken(snapshot.Power, snapshot.Battery, true);
        _ac.Value.Text = mains switch { true => Loc.T("已接入", "Connected"), false => Loc.T("未接入", "Unplugged"), _ => Loc.T("未知", "Unknown") };
        _ac.Note.Text = Loc.T("输入功率未知", "Input watts unknown");
        _ac.ToolTip = Loc.T("系统未提供充电器输入功率，电池充放电功率不等于充电器输入功率。", "Charger input power is not reported. Battery charge/discharge watts are not charger input watts.");
        _battery.NameText.Text = cell is null ? Loc.T("电池", "Battery") : Loc.T("电池 · ", "Battery · ") + Percent(cell.Percent);
        _battery.Value.Text = flow.BatteryWatts is { } bw ? Watts(bw) : cell is null ? "—" : cell.State switch
        { BatteryState.Full => Loc.T("已充满", "Full"), BatteryState.PluggedIn => Loc.T("未充电", "Not charging"), BatteryState.Charging => Loc.T("充电中", "Charging"), _ => Loc.T("放电中", "Discharging") };
        _battery.Note.Text = cell is null ? (snapshot.Battery.Reason.Length > 0 ? snapshot.Battery.Reason : Loc.T("暂无读数", "No reading"))
            : cell.State switch { BatteryState.Charging => Loc.T("正在充电", "Charging"), BatteryState.Discharging => Loc.T("正在放电", "Discharging"), _ => Loc.T("电池待机", "Battery idle") };
        _battery.ToolTip = PowerText.Battery(snapshot.Battery);
        _cpu.Value.Text = Watts(flow.Cpu); _gpu.Value.Text = Watts(flow.Gpu);
        _other.NameText.Text = flow.OtherIsMemory ? Loc.T("内存", "Memory") : Loc.T("其他硬件", "Other hardware");
        _other.Value.Text = (flow.Other is not null && !flow.OtherIsMemory ? "≈ " : "") + Watts(flow.Other);
        _cpu.ToolTip = PartsHelp(flow, "cpu"); _gpu.ToolTip = PartsHelp(flow, "gpu");
        _other.ToolTip = flow.OtherIsMemory ? PartsHelp(flow, "memory") : flow.Other is null
            ? Loc.T("缺少整机或分项读数，无法计算其他硬件功率。", "Other hardware power cannot be derived without system and component readings.")
            : Loc.T("整机功率减去 CPU 与 GPU 功率的差值，包含内存等未分项硬件，受采样时差影响。", "System power minus CPU and GPU, including memory and other unmetered hardware; sampling times can differ.");
        _curve.Set(flow.Total is null ? [] : snapshot.PowerHistory);
        _summary.Text = flow.Total is not null && snapshot.PowerSummary is { } summary
            ? Loc.T($"均 {PowerFormat.Watts(summary.Average)} · 峰 {PowerFormat.Watts(summary.Peak)} W", $"Avg {PowerFormat.Watts(summary.Average)} · Peak {PowerFormat.Watts(summary.Peak)} W") : Loc.T("暂无功耗趋势", "No power history");
        _summary.ToolTip = _summary.Text;
        bool shortfall = mains == true && flow.Discharging;
        _status.Text = shortfall ? Loc.T("已接通电源，电池仍在放电；无法确定充电器输入功率。", "AC is connected, but the battery is discharging; charger input power is unknown.")
            : flow.Estimated ? Loc.T("组件合计为估算，未计入屏幕、硬盘等未测量硬件。", "Component estimate; unmetered display, drives and other hardware are excluded.")
            : flow.Total is null ? Loc.T("暂无可读取的功耗数据。", "No power reading available.")
            : flow.Charging ? Loc.T("正在使用外接电源，电池正在充电。", "AC power connected; the battery is charging.")
            : mains == false ? Loc.T("正在使用电池供电。", "Running on battery power.")
            : mains is null ? Loc.T("供电状态未知。", "Power connection is unknown.")
            : Loc.T("正在使用外接电源。", "Running on AC power.");
        _status.Foreground = UI.Brush(shortfall ? _colors.Warning : UI.Secondary);
        _fanValue.Text = _fans && snapshot.Fans.OrDefault is { } fan ? Percent(fan.Fraction * 100) + " · " + FanSpeeds.Rpm(fan.Rpm)
            : _fans ? "—" : Loc.T("已关闭", "Off");
        _fanValue.ToolTip = snapshot.Fans.OrDefault is { } readFan ? FanSpeeds.Details(readFan) : snapshot.Fans.Reason;
        string Heat(double? value) => _temperatures && value is { } v && double.IsFinite(v) ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + " °C" : _temperatures ? "—" : Loc.T("已关闭", "Off");
        _cpuHeat.Text = Heat(snapshot.Cpu.OrDefault?.Temperature); _gpuHeat.Text = Heat(snapshot.Gpu.OrDefault?.Temperature);
        _source.Text = flow.Power is null ? Loc.T("来源：无可用功率计", "Source: no available meter") : flow.Power.Source switch
        {
            PowerSource.SystemMeter => Loc.T("来源：整机功率计", "Source: system meter"),
            PowerSource.Battery => Loc.T("来源：电池放电功率", "Source: battery discharge"),
            _ => Loc.T("来源：", "Source: ") + string.Join(" + ", flow.Power.Parts.Select(p => p.Name).Distinct())
        };
        _batteryInfo.Text = cell is null ? "" : string.Join(" · ", new[] {
            cell.Health is { } health ? Loc.T("电池健康度 ", "Battery health ") + Percent(health * 100) : "",
            cell.TimeLeft is { } left ? Loc.T("剩余 ", "Left ") + PowerFormat.Clock(left) : cell.TimeToFull is { } full ? Loc.T("充满需 ", "To full ") + PowerFormat.Clock(full) : BatteryModel.ModeName(cell.Mode)
        }.Where(s => s.Length > 0));
        _source.ToolTip = _source.Text; _batteryInfo.ToolTip = PowerText.Battery(snapshot.Battery);
        foreach (var node in new[] { _ac, _battery, _machine, _cpu, _gpu, _other }) node.UpdateAccessibleName();
        _ribbons.Set(flow, mains);
    }

    private static string PartsHelp(PowerFlow flow, string key)
    {
        var parts = flow.Power?.Parts.Where(p => p.Key == key).ToList() ?? [];
        return parts.Count > 0 ? string.Join("\n", parts.Select(p => p.Name + " · " + Watts(p.Watts) + " · " + p.Source))
            : key == "gpu" && flow.Power is { Missing.Count: > 0 } power ? string.Join("\n", power.Missing) : Loc.T("没有此分项的功率读数。", "No power reading for this component.");
    }
    public void SetActive(bool active) => _ribbons.SetActive(active && !PanelTheme.HighContrast);
}

internal sealed class PowerDetailNode : Border
{
    public StackPanel Body { get; } = new() { VerticalAlignment = VerticalAlignment.Center };
    public TextBlock NameText { get; }
    public TextBlock Value { get; }
    public TextBlock Note { get; }
    public PowerDetailNode(string id, string name, string glyph, string color, bool large = false)
    {
        Background = UI.Brush(UI.Fill); BorderBrush = UI.Brush(UI.Hairline); BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(PanelTheme.Corner(10)); Padding = new Thickness(7, large ? 7 : 4, 7, large ? 7 : 4); Child = Body;
        AutomationProperties.SetAutomationId(this, "power-node-" + id);
        var head = new Grid(); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new());
        var icon = UI.Line(glyph, large ? 18 : 13, color); icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        icon.Margin = new Thickness(0, 0, 5, 0); head.Children.Add(icon);
        NameText = UI.Line(name, 10, UI.Secondary); NameText.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(NameText, 1); head.Children.Add(NameText); Body.Children.Add(head);
        Value = UI.Line("—", large ? 25 : 15, UI.Primary, FontWeights.SemiBold);
        Value.TextTrimming = TextTrimming.None;
        // Down-only scaling protects the full numeric value on the narrowest panel and at either panel scale.
        var valueBox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Height = large ? 34 : 22, Child = Value };
        Body.Children.Add(valueBox);
        Note = UI.Line("", 10, UI.Tertiary); Note.TextTrimming = TextTrimming.CharacterEllipsis; Body.Children.Add(Note);
        if (large) { NameText.FontSize = 11; NameText.HorizontalAlignment = HorizontalAlignment.Center; valueBox.HorizontalAlignment = HorizontalAlignment.Center; }
    }
    public void UpdateAccessibleName()
    {
        Note.Visibility = Note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, NameText.Text + " " + Value.Text + " " + Note.Text);
        NameText.ToolTip = NameText.Text; Note.ToolTip = Note.Text;
    }
}

internal sealed class PowerDetailCurve(PowerStyle style) : FrameworkElement
{
    private IReadOnlyList<double> _history = [];
    public void Set(IReadOnlyList<double> history) { _history = history.Where(PowerResolver.Plausible).ToArray(); InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_history.Count < 2 || ActualWidth < 2) return;
        PowerGlyphs.Curve(drawing, new Rect(0, 1, ActualWidth, Math.Max(1, ActualHeight - 2)), _history, Math.Max(10, _history.Max() * 1.15), style);
    }
}

internal sealed class PowerFlowRibbons : FrameworkElement
{
    private readonly PowerDetailNode[] _nodes;
    private readonly LinearGradientBrush _forward, _backward;
    private readonly TranslateTransform _forwardPhase = new(), _backPhase = new();
    private PowerFlow? _flow;
    private bool? _mains;
    private bool _active;

    public PowerFlowRibbons(Grid diagram, PowerDetailNode ac, PowerDetailNode battery, PowerDetailNode machine, PowerDetailNode cpu, PowerDetailNode gpu, PowerDetailNode other, string color)
    {
        _nodes = [ac, battery, machine, cpu, gpu, other]; IsHitTestVisible = false;
        LinearGradientBrush Brush(TranslateTransform phase)
        {
            var c = ((SolidColorBrush)UI.Brush(color)).Color;
            Color Alpha(byte a) => Color.FromArgb(a, c.R, c.G, c.B);
            return new LinearGradientBrush(new GradientStopCollection { new(Alpha(20), 0), new(Alpha(55), .3), new(Alpha(92), .5), new(Alpha(20), .8), new(Alpha(20), 1) }, new Point(0, 0), new Point(1, 0))
            { RelativeTransform = phase, SpreadMethod = GradientSpreadMethod.Repeat };
        }
        _forward = Brush(_forwardPhase); _backward = Brush(_backPhase);
        SizeChanged += (_, _) => InvalidateVisual(); Unloaded += (_, _) => SetActive(false);
    }
    public void Set(PowerFlow flow, bool? mains) { _flow = flow; _mains = mains; InvalidateVisual(); }
    public void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;
        void Move(TranslateTransform phase, double to)
        {
            phase.BeginAnimation(TranslateTransform.XProperty, active ? new DoubleAnimation(0, to, TimeSpan.FromSeconds(3.5)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        }
        Move(_forwardPhase, 1); Move(_backPhase, -1);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        if (_flow is not { } flow || ActualWidth < 1 || _nodes.Any(n => n.ActualWidth < 1)) return;
        Rect Box(PowerDetailNode node) => new(node.TranslatePoint(new Point(), this), node.RenderSize);
        var machine = Box(_nodes[2]);
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (i == 2) continue;
            bool supply = i < 2;
            var node = Box(_nodes[i]);
            double fraction = supply ? (i + 1) / 3.0 : (i - 2) / 4.0;
            double x1 = supply ? node.Right : machine.Right, x2 = supply ? machine.Left : node.Left;
            double y1 = supply ? node.Top + node.Height / 2 : machine.Top + machine.Height * fraction;
            double y2 = supply ? machine.Top + machine.Height * fraction : node.Top + node.Height / 2;
            double? watts = i switch { 0 => null, 1 => flow.BatteryWatts, 3 => flow.Cpu, 4 => flow.Gpu, _ => flow.Other };
            bool running = i == 0 ? _mains == true && flow.Total is not null : watts is > 0;
            double half = running ? 3 + Math.Clamp((watts ?? 0) / Math.Max(1, flow.Total ?? 1), 0, 1) * 8 : .5;
            double mid = (x1 + x2) / 2;
            var shape = new StreamGeometry();
            using (var p = shape.Open())
            {
                p.BeginFigure(new Point(x1, y1 - half), true, true);
                p.BezierTo(new Point(mid, y1 - half), new Point(mid, y2 - half), new Point(x2, y2 - half), true, false);
                p.LineTo(new Point(x2, y2 + half), true, false);
                p.BezierTo(new Point(mid, y2 + half), new Point(mid, y1 + half), new Point(x1, y1 + half), true, false);
            }
            shape.Freeze();
            drawing.DrawGeometry(running ? i == 1 && flow.Charging ? _backward : _forward : UI.Brush(UI.Hairline), null, shape);
        }
    }
}
