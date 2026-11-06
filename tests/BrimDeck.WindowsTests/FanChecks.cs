using BrimDeck.Core;
using BrimDeck.Native;

static class FanChecks
{
    public static void Run(Action<string, bool> check)
    {
        using var reader = new ClevoFans();
        var off = reader.Sample(false, 5750);
        check("Fans: disabled provider does not probe hardware", !off.HasValue && reader.State == SensorState.Unknown);
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) Thread.Sleep(2100);
            var reading = reader.Sample(true, 5750);
            check($"Fans: native sample {i + 1} reads both fan RPMs", reading.TryGet(out var sample) && sample.CpuRpm is >= 0 and <= 15000 && sample.GpuRpm is >= 0 and <= 15000);
            Console.WriteLine($"CPU {sample.CpuRpm} RPM; GPU {sample.GpuRpm} RPM; F {sample.Fraction:P0}");
            var cached = reader.Sample(true, 6000);
            check("Fans: cached telemetry immediately reflects a changed maximum", cached.TryGet(out var same) && same.CpuRpm == sample.CpuRpm && same.GpuRpm == sample.GpuRpm && same.MaximumRpm == 6000);
        }
        check("Fans: stopping clears cached values", !reader.Sample(false, 5750).HasValue);
    }
}
