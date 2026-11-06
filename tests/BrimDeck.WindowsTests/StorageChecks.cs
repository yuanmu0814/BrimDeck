using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
using BrimDeck.Core;
using BrimDeck.Native;

static class StorageChecks
{
    public static void Helper(Action<string, bool> check)
    {
        check("USB service: unknown setup actions are rejected before any mutation", UsbTemperatureSetup.RunElevated(["unexpected"]) == 2);
        check("USB service: the client rejects out-of-range disks", UsbTemperatureAccess.Read(-1, "invalid") is null && UsbTemperatureAccess.Read(64, "invalid") is null);
        bool wasEnabled = UsbTemperatureAccess.Enabled;
        UsbTemperatureAccess.Enabled = false;
        check("USB service: a paused client reads nothing", !UsbTemperatureAccess.Active && UsbTemperatureAccess.Read(2, "USB") is null);
        UsbTemperatureAccess.Enabled = wasEnabled;
        int? internalDisk = StorageReader.Volumes().Select(volume => volume.Disk).OfType<int>().Cast<int?>().FirstOrDefault(number => StorageReader.Describe(number!.Value)?.Bus != Storage.BusUsb);
        if (internalDisk is { } number) check("USB service: an internal disk is rejected before SMART access", UsbDiskTemperature.Read(number) is null);

        string name = "BrimDeck.UsbSensors.Tests." + Guid.NewGuid().ToString("N");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int calls = 0;
        var server = UsbTemperatureServer.RunAsync(name, disk => { Interlocked.Increment(ref calls); return disk == 2 ? 42 : 999; }, cancel.Token);
        int? Ask(int disk)
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(2000);
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, disk);
            pipe.WriteAsync(bytes, cancel.Token).AsTask().GetAwaiter().GetResult();
            try { pipe.ReadExactlyAsync(bytes, cancel.Token).AsTask().GetAwaiter().GetResult(); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }
            catch (System.IO.EndOfStreamException) { return null; }
        }
        try
        {
            check("USB service: a local client receives Celsius", Ask(2) == 42);
            check("USB service: repeated disk reads use the 30-second cache", Ask(2) == 42 && calls == 1);
            check("USB service: invalid temperatures become unavailable and are cached", Ask(3) == 0 && Ask(3) == 0 && calls == 2);
            check("USB service: invalid disk numbers never reach the reader", Ask(-1) is null && Ask(64) is null && calls == 2);
            // An incomplete request cannot occupy the only channel indefinitely.
            using (var partial = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                partial.Connect(2000); partial.WriteByte(2);
                check("USB service: incomplete requests time out", partial.ReadAsync(new byte[1], cancel.Token).AsTask().GetAwaiter().GetResult() == 0);
            }
            check("USB service: normal reads recover after an incomplete request", Ask(2) == 42 && calls == 2);
        }
        finally { cancel.Cancel(); }
        check("USB service: cancellation releases the server", server.Wait(TimeSpan.FromSeconds(3)));
        var idle = UsbTemperatureServer.RunAsync(name, _ => throw new InvalidOperationException("No request expected"), CancellationToken.None, idleSeconds: 0.2);
        check("USB service: an idle server exits without reading hardware", idle.Wait(TimeSpan.FromSeconds(3)));
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
