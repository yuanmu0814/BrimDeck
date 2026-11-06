using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BrimDeck.Core;
using Microsoft.Win32.SafeHandles;

namespace BrimDeck.Native;

// Ordinary-permission client of the persistent USB-only service. Its registered start/query rights survive Windows restarts.
// Opening the monitor starts the service on demand; sampling and normal app startup never request UAC.
internal static class UsbTemperatureAccess
{
    private static bool _enabled = true;
    private static int _generation, _seenSetup = -1;
    private static bool _installed;
    private static double _retryAt;
    private static IReadOnlyList<StorageDrive> _latest = [];
    public static int Generation => Volatile.Read(ref _generation) + UsbTemperatureSetup.Generation;
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set { if (value == Enabled) return; Volatile.Write(ref _enabled, value); Interlocked.Increment(ref _generation); }
    }
    public static bool Active => Enabled && UsbTemperatureSetup.Installed;
    public static IReadOnlyList<StorageDrive> Latest => Volatile.Read(ref _latest);
    public static async Task EnableAsync()
    {
        await UsbTemperatureSetup.InstallAsync().ConfigureAwait(false);
        Enabled = true; Interlocked.Increment(ref _generation);
        await ProbeAsync().ConfigureAwait(false);
    }
    public static async Task ProbeAsync()
    {
        var reader = new StorageReader();
        reader.Sample(Now(), true);
        await reader.Pending.WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
        Volatile.Write(ref _latest, reader.Sample(Now(), true).Where(drive => drive.External).ToArray());
    }
    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static DiskTemperature? Read(int number, string model)
    {
        if (!Enabled || number is < 0 or >= 64) return null;
        if (_seenSetup != UsbTemperatureSetup.Generation) { _installed = UsbTemperatureSetup.Installed; _seenSetup = UsbTemperatureSetup.Generation; _retryAt = 0; }
        if (!_installed || Now() < _retryAt) return null;
        try
        {
            UsbTemperatureSetup.Start();
            using var pipe = new NamedPipeClientStream(".", UsbTemperatureServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            pipe.Connect(TimeSpan.FromSeconds(1.5));
            // A process occupying the pipe name must not be mistaken for the registered SYSTEM service.
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer == 0 || peer != UsbTemperatureSetup.ProcessId) return null;
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, number);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.ReadExactlyAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            return Core.Temperatures.Disk(model, BinaryPrimitives.ReadInt32LittleEndian(bytes), 0, 0);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        { _retryAt = Now() + 30; return null; }
    }
    public static void Stop() { Enabled = false; Volatile.Write(ref _latest, []); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
