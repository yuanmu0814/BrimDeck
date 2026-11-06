using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using BrimDeck.Core;

namespace BrimDeck.Native;

// A session-only elevated helper for USB bridges that need SMART pass-through access. The ordinary app owns a random,
// current-user-only pipe; the helper cannot impersonate its server. Its only request is a physical disk number, checked again
// against Windows' USB bus descriptor before a read-only SMART command. It never initializes the app, AI engine or settings,
// installs anything, or changes device permissions. Closing the pipe or the parent process ends the helper.
internal static class UsbTemperatureAccess
{
    public const string ElevatedSwitch = "--usb-temperature-helper";
    private const string Prefix = "BrimDeck.UsbTemperature.";
    private static readonly SemaphoreSlim Gate = new(1);
    private static NamedPipeServerStream? _pipe;
    private static Process? _helper;
    private static int _generation;
    private static IReadOnlyList<StorageDrive> _latest = [];
    public static int Generation => Volatile.Read(ref _generation);
    public static bool Active => Volatile.Read(ref _pipe)?.IsConnected == true;
    public static IReadOnlyList<StorageDrive> Latest => Volatile.Read(ref _latest);

    public static async Task EnableAsync(bool elevate = true)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Active) return;
            Stop();
            string name = Prefix + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = elevate ? "runas" : "", WindowStyle = ProcessWindowStyle.Hidden
                };
                info.ArgumentList.Add(ElevatedSwitch); info.ArgumentList.Add(name);
                info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _helper = await Task.Run(() => Process.Start(info)).ConfigureAwait(false);
                if (_helper is null) throw new InvalidOperationException(Loc.T("无法启动 USB 温度采集助手", "Could not start the USB temperature helper"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                Volatile.Write(ref _pipe, pipe);
                Interlocked.Increment(ref _generation);
            }
            catch
            {
                pipe.Dispose(); Stop(); throw;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException(Loc.T("未取得管理员授权", "Administrator permission was not given")); }
        finally { Gate.Release(); }
        // Verify the granted channel immediately, so settings can show real temperatures instead of only an "enabled" status.
        var reader = new StorageReader();
        reader.Sample(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, true);
        await reader.Pending.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var sample = reader.Sample(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, true);
        Volatile.Write(ref _latest, sample.Where(drive => drive.External).ToArray());
        Interlocked.Increment(ref _generation);
    }

    // Called only on StorageReader's background thread, inside its per-drive guard and at its temperature cadence.
    public static DiskTemperature? Read(int number, string model)
    {
        if (!Active || number is < 0 or >= 64 || !Gate.Wait(TimeSpan.FromSeconds(1))) return null;
        try
        {
            if (Volatile.Read(ref _pipe) is not { IsConnected: true } pipe) return null;
            var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, number);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            pipe.ReadExactlyAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
            return Core.Temperatures.Disk(model, BinaryPrimitives.ReadInt32LittleEndian(bytes), 0, 0);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { Stop(); return null; }
        finally { Gate.Release(); }
    }

    public static void Stop()
    {
        Interlocked.Exchange(ref _pipe, null)?.Dispose();
        Interlocked.Exchange(ref _helper, null)?.Dispose();
        Volatile.Write(ref _latest, []);
        Interlocked.Increment(ref _generation);
    }

    // The same EXE enters this mode before constructing App. Fixed-size messages carry numbers only, no paths or commands.
    public static async Task<int> RunHelperAsync(string name, string parentId)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _)
            || !int.TryParse(parentId, out int id) || id <= 0) return 2;
        try
        {
            using var parent = Process.GetProcessById(id);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 2;
            using var lifetime = new CancellationTokenSource();
            _ = parent.WaitForExitAsync().ContinueWith(_ => { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(15000, lifetime.Token).ConfigureAwait(false);
            var bytes = new byte[4];
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(bytes, lifetime.Token).ConfigureAwait(false);
                int number = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                if (number is < 0 or >= 64) return 2;
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                reading.CancelAfter(TimeSpan.FromSeconds(7));
                int celsius = await Task.Run(() => ReadUsb(number)).WaitAsync(reading.Token).ConfigureAwait(false);
                BinaryPrimitives.WriteInt32LittleEndian(bytes, celsius);
                await pipe.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or InvalidOperationException or Win32Exception)
        { return 0; }
    }

    private static int ReadUsb(int number) => StorageReader.Describe(number) is { Bus: Storage.BusUsb } disk
        && UsbDiskTemperature.Read(number, disk.Model) is { } heat ? (int)heat.Celsius : 0;
}
