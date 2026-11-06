using System.Globalization;

namespace BrimDeck.Core;

public sealed record FanSample(int CpuRpm, int GpuRpm, int MaximumRpm = FanSpeeds.DefaultMaximumRpm)
{
    public int Rpm => Math.Max(CpuRpm, GpuRpm);
    public double Fraction => FanSpeeds.Fraction(Rpm, MaximumRpm);
}

public static class FanSpeeds
{
    public const int DefaultMaximumRpm = 5750;
    // Control Center's period counter and display formula, verified on V15x_V17xPNKPNJPNH.
    public static int FromPeriod(int period) => period <= 0 ? 0 : (int)Math.Round(60 / (5.565217391304348e-5 * period) * 2);
    public static double Fraction(int rpm, int maximumRpm) => maximumRpm > 0 ? Math.Clamp((double)rpm / maximumRpm, 0, 1) : 0;
    public static string Rpm(int rpm) => rpm.ToString(CultureInfo.InvariantCulture) + " RPM";
    public static string Details(FanSample sample) => $"CPU {Rpm(sample.CpuRpm)} · {FanSpeeds.Fraction(sample.CpuRpm, sample.MaximumRpm):P0}\nGPU {Rpm(sample.GpuRpm)} · {FanSpeeds.Fraction(sample.GpuRpm, sample.MaximumRpm):P0}";
}

// The dial always shows the rotation-speed share; this chooses the number following F.
public static class CompactFans
{
    public const string Rpm = "Rpm", Ratio = "Ratio";
    public static bool ShowsRpm(CompactReading reading) => reading.Figures?.Contains(Rpm) == true;
    public static List<string>? Normalize(IEnumerable<string?>? figures) =>
        figures?.Any(figure => string.Equals(figure?.Trim(), Rpm, StringComparison.OrdinalIgnoreCase)) == true ? [Rpm] : null;
}
