using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck.Native;

// Read-only Control Center telemetry for the board whose period layout has been verified.
// No setter exports, ACPI writes, mode changes or fan-control commands are used.
internal sealed class ClevoFans(Action? stuck = null) : IDisposable
{
    private const string Device = "clevo-control-center";
    private const int BufferSize = 4096;
    private static readonly GuardedDevice Guard = new(GuardedReading.Fans, Device, "Control Center fans");
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetBuffer(int command, IntPtr buffer);
    private IntPtr _library;
    private GetBuffer? _get;
    private bool _looked;
    private double _nextRead = double.NegativeInfinity;
    private Reading<FanSample> _last = Reading<FanSample>.Unavailable(Why.Waiting);
    public SensorState State { get; private set; }

    public Reading<FanSample> Sample(bool wanted, int maximumRpm)
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!wanted) { Rest(); return _last; }
        if (SensorGuard.Stopped(GuardedReading.Fans, Device))
        {
            State = SensorState.None;
            return _last = Reading<FanSample>.Unavailable("Fan reading stopped");
        }
        if (now < _nextRead) return _last.Map(sample => sample with { MaximumRpm = maximumRpm });
        _nextRead = now + 2;
        try
        {
            if (!_looked) Open();
            if (_get is null) return _last = Reading<FanSample>.Unavailable("No supported fan telemetry");
            _last = SensorGuard.Ask(Guard, () => Read(maximumRpm), stuck);
            if (_get is not null) State = _last.HasValue ? SensorState.Found : SensorState.Unknown;
        }
        catch (ReadingAbandoned) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Fan telemetry: {ex.GetType().Name}");
            _last = Reading<FanSample>.Unavailable("Fan telemetry unavailable");
        }
        finally { _nextRead = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + 2; }
        return _last;
    }

    private void Open()
    {
        _looked = true;
        State = SensorState.None;
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (!string.Equals(bios?.GetValue("BaseBoardProduct") as string, "V15x_V17xPNKPNJPNH", StringComparison.OrdinalIgnoreCase)) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ControlCenter", "InsydeDCHU.dll");
        if (!File.Exists(path)) return;
        _library = NativeLibrary.Load(path, typeof(ClevoFans).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        _get = Marshal.GetDelegateForFunctionPointer<GetBuffer>(NativeLibrary.GetExport(_library, "GetDCHU_Data_Buffer"));
        State = SensorState.Unknown;
    }

    private Reading<FanSample> Read(int maximumRpm)
    {
        // The vendor DLL writes byte 256 too, outside its nominal 256-byte payload.
        // Leave ample space and stop this provider if writes reach the guard region.
        var bytes = new byte[BufferSize]; Array.Fill(bytes, (byte)0xCD);
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            _get!(12, buffer); // Runtime telemetry getter only.
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        if (bytes.AsSpan(512).ContainsAnyExcept((byte)0xCD))
        {
            _get = null; State = SensorState.None;
            return Reading<FanSample>.Unavailable("Unsupported fan buffer layout");
        }
        var data = bytes.AsSpan(0, 256);
        if (!data.ContainsAnyExcept((byte)0xCD) || !data.ContainsAnyExcept((byte)0))
            return Reading<FanSample>.Unavailable("Fan telemetry not ready");
        int cpu = FanSpeeds.FromPeriod((data[2] << 8) | data[3]), gpu = FanSpeeds.FromPeriod((data[4] << 8) | data[5]);
        if (cpu > 15000 || gpu > 15000)
        {
            _get = null; State = SensorState.None;
            return Reading<FanSample>.Unavailable("Unsupported fan period layout");
        }
        return Reading<FanSample>.Of(new(cpu, gpu, maximumRpm));
    }

    public void Rest() => _last = Reading<FanSample>.Unavailable(Why.Waiting);
    public void Dispose() { if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; } }
}
