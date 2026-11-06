using System.Diagnostics;
using BrimDeck.Core;
using BrimDeck.Native;

static class StorageChecks
{
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
