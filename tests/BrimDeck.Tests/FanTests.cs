using System.Text.Json;
using BrimDeck.Core;

static class FanTests
{
    public static void Run(Action<string, bool> check)
    {
        const CompactMonitorItems F = CompactMonitorItems.Fans;
        check("Fans: vendor periods convert to RPM, including a stopped fan", FanSpeeds.FromPeriod(1000) == 2156 && FanSpeeds.FromPeriod(0) == 0 && FanSpeeds.FromPeriod(453) == 4760);
        var sample = new FanSample(4760, 4607);
        check("Fans: F uses the faster fan and the 5750 RPM maximum", sample.Rpm == 4760 && Math.Abs(sample.Fraction - 4760d / 5750) < 1e-9
            && new FanSample(4607, 4760).Fraction == sample.Fraction);
        check("Fans: dial shares stay within the ring", FanSpeeds.Fraction(6000, 5750) == 1 && FanSpeeds.Fraction(-1, 5750) == 0 && FanSpeeds.Fraction(2000, 0) == 0);
        check("Fans: both fan readings remain available in the details", FanSpeeds.Details(sample).Contains("CPU 4760 RPM") && FanSpeeds.Details(sample).Contains("GPU 4607 RPM"));
        var settings = new DeckSettings { CompactReadings = [new() { Item = F, Figures = [" rpm "] }] };
        settings.Normalize();
        var restored = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        check("Fans: RPM display choice survives normalization and saving", CompactFans.ShowsRpm(restored.CompactReadings.First(r => r.Item == F)) && restored.FanMaximumRpm == 5750);
        restored.FanMaximumRpm = 0; restored.Normalize();
        check("Fans: an invalid maximum returns to the default", restored.FanMaximumRpm == 5750);
        restored.FanMaximumRpm = 6000; restored.Normalize();
        check("Fans: a valid maximum is retained", restored.FanMaximumRpm == 6000);
        var ratio = CompactMonitor.Normalize([new() { Item = F, Figures = [CompactFans.Ratio, "unknown"] }]).First(r => r.Item == F);
        check("Fans: ratio is the default and a figure can be hidden", !CompactFans.ShowsRpm(ratio) && CompactMonitor.HasValueChoice(F)
            && !CompactMonitor.HasValueChoice(F | CompactMonitorItems.Cpu) && CompactMonitor.RingsOnly(F, CompactMonitorItems.None));
        var readings = CompactMonitor.Normalize([new() { Item = F }]);
        var admission = new StripAdmission();
        check("Fans: a fan-only strip requests its first reading", admission.Awaits(readings, null, SensorStates.Unknown) && (admission.Hidden & F) != 0);
        var snapshot = MonitorSnapshot.Empty(4, "test") with { Fans = sample };
        var (hidden, arriving) = admission.Update(snapshot, readings, SensorStates.Unknown);
        check("Fans: first fan reading admits the dial", (hidden & F) == 0 && (arriving & F) != 0);
        (hidden, arriving) = admission.Update(snapshot with { Fans = Reading<FanSample>.Unavailable("temporary") }, readings, SensorStates.Unknown);
        check("Fans: a transient miss keeps the slot", (hidden & F) == 0 && arriving == CompactMonitorItems.None);
        (hidden, _) = admission.Update(null, readings, SensorStates.Unknown with { Fans = SensorState.None });
        check("Fans: unsupported hardware stays hidden without waiting", (hidden & F) != 0 && !admission.Awaits(readings, null, SensorStates.Unknown with { Fans = SensorState.None }));
        var width = CompactMonitor.Widths(F, 16, _ => 70, 16, 20, 40);
        check("Fans: layout reserves the complete RPM figure", width(F) == 86 && CompactMonitor.Width(F, width, 6) == 86 && CompactMonitor.Fit(F, width, 6, 85) == CompactMonitorItems.None);
        CompactMonitor.Apply(readings, CompactMonitorMode.Brief);
        check("Fans: Brief remains the original three rings", CompactMonitor.Shown(readings) == CompactMonitor.Rings && !readings.First(r => r.Item == F).Visible);
        CompactMonitor.Apply(readings, CompactMonitorMode.Full);
        check("Fans: Full includes the fan dial and its figure", (CompactMonitor.Shown(readings) & F) != 0 && (CompactMonitor.Values(readings) & F) != 0);
        Layout(check);
    }

    private static void Layout(Action<string, bool> check)
    {
        var normal = MonitorLayout.For(871, 250, true, 2, 1, fans: true);
        var parts = normal.Parts(250);
        check("Fans page: both fan readings have a line below power", normal.FanLine && parts[6].Top == parts[4].Top + parts[4].Height && parts[6].Height > 0);
        var disabled = MonitorLayout.For(871, 250, true, 2, 1);
        check("Fans page: disabled or unsupported fans reserve no row", !disabled.FanLine && disabled.Parts(250)[6].Height == 0 && disabled == MonitorLayout.For(871, 250, true, 2, 1, fans: false));
        check("Fans page: very short pages retain a usable graph", !MonitorLayout.For(400, 90, false, 2, 1, fans: true).FanLine);
        bool fits = true;
        foreach (double width in new[] { 360d, 700, 960 })
        for (double height = 100; height <= 420; height += 5)
        {
            var layout = MonitorLayout.For(width, height, true, 6, 1, frames: true, fans: true);
            double readings = layout.List == ListPlacement.Below ? MonitorLayout.StackedReadings : height;
            var blocks = layout.Parts(readings);
            if (layout.FanLine && (blocks[1].Height < MonitorLayout.MinimumGraph || blocks[6].Top + blocks[6].Height > readings + 1e-9)) fits = false;
        }
        check("Fans page: fans, frames, drives and power fit without cutting the graph", fits);
        var settings = new DeckSettings(); settings.Normalize();
        check("Fans page: enabled by default independently of the capsule", settings.MonitorFans && !settings.CompactReadings.First(r => r.Item == CompactMonitorItems.Fans).Visible);
        settings.MonitorFans = false;
        var restored = JsonSerializer.Deserialize<DeckSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        check("Fans page: its separate visibility switch is saved", !restored.MonitorFans);
    }
}
