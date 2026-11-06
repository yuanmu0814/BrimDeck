using System.ServiceProcess;
using BrimDeck.Native;

namespace BrimDeck.UsbSensors;

internal static class Program
{
    public static void Main() => ServiceBase.Run(new UsbSensorService());
}

// On-demand service, installed in protected Program Files once. No UI, AI engine, account files or driver dependency.
internal sealed class UsbSensorService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    public UsbSensorService() { ServiceName = "BrimDeckUsbSensors"; CanStop = true; CanShutdown = true; }
    protected override void OnStart(string[] args)
    {
        _worker = Task.Run(async () =>
        {
            try { await UsbTemperatureServer.RunAsync(UsbTemperatureServer.PipeName, Read, _stop.Token); }
            finally { if (!_stop.IsCancellationRequested) ThreadPool.QueueUserWorkItem(_ => Stop()); }
        });
    }
    private static int Read(int number) => UsbDiskTemperature.Read(number) is { } heat ? (int)heat.Celsius : 0;
    protected override void OnStop() { _stop.Cancel(); try { _worker?.Wait(3000); } catch (AggregateException) { } }
    protected override void OnShutdown() => OnStop();
}
