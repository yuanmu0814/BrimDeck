namespace BrimDeck.Core;

// Projects existing readings; component estimates never become a system meter. Battery rate is not charger input or USB power.
public sealed record PowerFlow(PowerSample? Power, BatterySample? Battery, double? Cpu, double? Gpu, double? Other, bool OtherIsMemory)
{
    public double? Total => Power?.Watts;
    public bool Estimated => Power?.Estimated == true;
    public double? BatteryWatts => Battery is { HasRate: true } b && PowerResolver.Plausible(b.Watts) ? b.Watts : null;
    public bool Charging => Battery?.State == BatteryState.Charging;
    public bool Discharging => Battery?.State == BatteryState.Discharging;

    public static PowerFlow From(Reading<PowerSample> power, Reading<BatterySample> battery)
    {
        var sample = power.OrDefault is { } p && PowerResolver.Plausible(p.Watts) ? p : null;
        var cell = battery.OrDefault is { } b && double.IsFinite(b.Percent) && b.Percent is >= 0 and <= 100 ? b : null;
        double? Part(string key)
        {
            if (sample is null) return null;
            var parts = sample.Parts.Where(part => part.Key == key).ToList();
            return parts.Count > 0 && parts.All(part => PowerResolver.Plausible(part.Watts)) && PowerResolver.Plausible(parts.Sum(part => part.Watts))
                ? parts.Sum(part => part.Watts) : null;
        }
        var cpu = Part("cpu"); var gpu = Part("gpu"); var memory = Part("memory");
        // A true platform/battery reading includes RAM. Without both CPU/GPU, or when they exceed the total, no residual is known.
        double? other = sample is { Estimated: false } && cpu is not null && gpu is not null && cpu + gpu <= sample.Watts
            ? sample.Watts - cpu - gpu : null;
        bool memoryOnly = sample?.Estimated == true && memory is not null;
        if (memoryOnly) other = memory;
        return new(sample, cell, cpu, gpu, other, memoryOnly);
    }
}
