using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BrimDeck.Native;

// Fixed-size, local-only protocol: disk number in, Celsius (zero means unavailable) out. No paths, commands or writes accepted.
// The reader verifies the USB bus. Cache all results for 30 seconds so repeated clients cannot keep querying a disk.
internal static class UsbTemperatureServer
{
    public const string PipeName = "BrimDeck.UsbTemperatures.v1";
    public static async Task RunAsync(string name, Func<int, int> read, CancellationToken cancel, double idleSeconds = 60)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var clock = Stopwatch.StartNew();
        double lastAsked = 0;
        var cache = new Dictionary<int, (double At, int Value)>();
        while (!cancel.IsCancellationRequested)
        {
            double remaining = idleSeconds - (clock.Elapsed.TotalSeconds - lastAsked);
            if (remaining <= 0) break;
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64, 64, security);
                using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    waiting.CancelAfter(TimeSpan.FromSeconds(remaining));
                    await pipe.WaitForConnectionAsync(waiting.Token).ConfigureAwait(false);
                }
                var bytes = new byte[4];
                using (var request = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    request.CancelAfter(TimeSpan.FromSeconds(2));
                    await pipe.ReadExactlyAsync(bytes, request.Token).ConfigureAwait(false);
                }
                int number = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                if (number is < 0 or >= 64) continue;
                lastAsked = clock.Elapsed.TotalSeconds;
                if (!cache.TryGetValue(number, out var result) || lastAsked - result.At >= 30)
                {
                    using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    reading.CancelAfter(TimeSpan.FromSeconds(7));
                    // A driver ignoring its timeout must not leave the service stuck: let its process exit.
                    int value;
                    try { value = await Task.Run(() => read(number)).WaitAsync(reading.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    result = (lastAsked, value is >= 1 and <= 125 ? value : 0); cache[number] = result;
                }
                BinaryPrimitives.WriteInt32LittleEndian(bytes, result.Value);
                using var reply = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                reply.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.WriteAsync(bytes, reply.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { if (cancel.IsCancellationRequested) break; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { break; }
        }
    }
}
