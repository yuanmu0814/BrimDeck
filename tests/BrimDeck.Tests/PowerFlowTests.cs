using BrimDeck.Core;

static class PowerFlowTests
{
    public static void Run(Action<string, bool> check)
    {
        Reading<BatterySample> absent = Reading<BatterySample>.Unavailable("no battery");
        PowerPart[] parts = [new("cpu", "CPU", 24.6, "CPU meter"), new("gpu", "GPU", 6.8, "GPU meter"), new("memory", "RAM", 4, "RAM meter")];
        var measured = new PowerSample(41.8, PowerOrigin.Measured, PowerSource.SystemMeter, parts);
        var flow = PowerFlow.From(measured, absent);
        check("Power flow: residual includes RAM without counting it twice", flow.Other is { } rest && Math.Abs(rest - 10.4) < .00001 && !flow.OtherIsMemory && !flow.Estimated);
        var estimate = PowerFlow.From(new PowerSample(35.4, PowerOrigin.Estimated, PowerSource.Components, parts), absent);
        check("Power flow: component estimate shows RAM, never fabricates other hardware", estimate.Estimated && estimate.OtherIsMemory && estimate.Other == 4);
        var cpuOnly = PowerFlow.From(measured with { Origin = PowerOrigin.Estimated, Source = PowerSource.Components, Watts = 24.6, Parts = [parts[0]] }, absent);
        check("Power flow: CPU-only source leaves GPU and other unknown", cpuOnly.Cpu == 24.6 && cpuOnly.Gpu is null && cpuOnly.Other is null);
        check("Power flow: missing GPU does not manufacture a system residual", PowerFlow.From(measured with { Parts = [parts[0]] }, absent).Other is null);
        check("Power flow: asynchronous parts above the total do not create negative hardware watts", PowerFlow.From(measured with { Watts = 20 }, absent).Other is null);
        var dual = PowerFlow.From(measured with { Parts = [parts[0], parts[1], new("gpu", "GPU 2", 2, "driver")] }, absent);
        check("Power flow: separate GPU boards add once each", dual.Gpu == 8.8 && Math.Abs(dual.Other!.Value - 8.4) < .00001);
        check("Power flow: unknown readings stay unknown instead of zero", PowerFlow.From(Reading<PowerSample>.Unavailable("unsupported"), absent) is { Total: null, Cpu: null, Gpu: null, Other: null, Battery: null });
        check("Power flow: invalid meters never become visible totals", new[] { double.NaN, double.PositiveInfinity, -1, 2001 }.All(v => PowerFlow.From(measured with { Watts = v }, absent).Total is null));
        check("Power flow: broken component readings invalidate that part and its residual", PowerFlow.From(measured with { Parts = [parts[0], parts[1] with { Watts = double.NaN }] }, absent) is { Gpu: null, Other: null });
        var cell = new BatterySample(64, BatteryState.Charging, 1) { HasRate = true, Watts = 8.6, AcOnline = true };
        var charging = PowerFlow.From(measured, cell);
        check("Power flow: battery charging is a separate output, never added to system watts", charging.Charging && !charging.Discharging && charging.BatteryWatts == 8.6 && charging.Total == 41.8);
        var pluggedDischarge = PowerFlow.From(measured, cell with { State = BatteryState.Discharging, Watts = 9.6 });
        check("Power flow: plugged-in battery discharge remains a shortfall, not total system consumption", pluggedDischarge.Discharging && !pluggedDischarge.Charging && pluggedDischarge.Total == 41.8);
        check("Power flow: battery rates without a valid reading are unknown", PowerFlow.From(measured, cell with { HasRate = false }).BatteryWatts is null && PowerFlow.From(measured, cell with { Watts = double.NaN }).BatteryWatts is null);
        check("Power flow: invalid battery share cannot masquerade as a valid battery", PowerFlow.From(measured, cell with { Percent = double.NaN }).Battery is null && PowerFlow.From(measured, cell with { Percent = 101 }).Battery is null);
        var batterySource = PowerFlow.From(measured with { Source = PowerSource.Battery }, cell with { State = BatteryState.Discharging, AcOnline = false, Watts = 41.8 });
        check("Power flow: genuine battery total stays measured with a valid residual", !batterySource.Estimated && batterySource.Other is not null && batterySource.Discharging);
    }
}
