using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Native;

static class StorageChecks
{
    public static void Helper(Action<string, bool> check)
    {
        check("USB helper: malformed pipe and parent arguments are rejected", UsbTemperatureAccess.RunHelperAsync("unexpected", "0").GetAwaiter().GetResult() == 2);
        UsbTemperatureAccess.EnableAsync(elevate: false).GetAwaiter().GetResult();
        check("USB helper: an isolated current-user pipe connects", UsbTemperatureAccess.Active);
        int? internalDisk = StorageReader.Volumes().Select(volume => volume.Disk).OfType<int>().Cast<int?>().FirstOrDefault(number => StorageReader.Describe(number!.Value)?.Bus != Storage.BusUsb);
        if (internalDisk is { } number) check("USB helper: an internal disk is never read through USB SMART", UsbTemperatureAccess.Read(number, "internal") is null);
        check("USB helper: out-of-range requests are rejected", UsbTemperatureAccess.Read(-1, "invalid") is null && UsbTemperatureAccess.Read(64, "invalid") is null);
        var child = (Process)typeof(UsbTemperatureAccess).GetField("_helper", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        int childId = child.Id;
        UsbTemperatureAccess.Stop();
        check("USB helper: closing the channel disables it", !UsbTemperatureAccess.Active);
        bool exited;
        try { using var oldChild = Process.GetProcessById(childId); exited = oldChild.WaitForExit(5000); }
        catch (ArgumentException) { exited = true; }
        check("USB helper: the owned process exits when its channel closes", exited);
    }

    public static void Temperatures(Action<string, bool> check, string[] expectedUsb)
    {
        var reader = new StorageReader();
        static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        reader.Sample(Now(), true);
        check("Storage: background temperature discovery completes", reader.Pending.Wait(TimeSpan.FromSeconds(30)));
        var drives = reader.Sample(Now(), true);
        check("Storage: temperature readings belong to their physical drives", drives.All(d => d.Temperature is not { } heat || heat.Number == d.Number));
        foreach (var letter in expectedUsb)
            check($"Storage: USB volume {letter} reports its temperature", Storage.Find(drives, letter) is { Drive: { External: true, Temperature: not null } });
        foreach (var drive in drives)
            Console.WriteLine($"{drive.Letters} temperature={drive.Temperature?.Celsius.ToString("0") ?? "unavailable"} °C; external={drive.External}");
    }

    public static void Run(Action<string, bool> check, string[] expectedUsb)
    {
        var reader = new StorageReader();
        var clock = Stopwatch.StartNew();
        reader.Start(false);
        check("Storage: background capacity discovery completes", reader.Pending.Wait(TimeSpan.FromSeconds(30)));
        var drives = reader.Listed;
        check("Storage: all listed volumes have usable capacity", drives.Count > 0 && drives.SelectMany(d => d.Volumes).All(v => v.Total > 0 && v.Free <= v.Total));
        check("Storage: volume count matches discovered drives", reader.Count == Storage.VolumeCount(drives));
        check("Storage: capacity-only discovery requests no temperatures", drives.All(d => d.Temperature is null));
        foreach (var letter in expectedUsb)
            check($"Storage: USB volume {letter} is included", Storage.Find(drives, letter) is { Drive.External: true });
        foreach (var drive in drives)
            Console.WriteLine($"{drive.Letters} {drive.Total / (double)(1UL << 30):0.0} GB; external={drive.External}");
        Console.WriteLine($"Capacity scan completed in {clock.Elapsed.TotalMilliseconds:0} ms.");
    }
}
