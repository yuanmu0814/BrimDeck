using System.Diagnostics;
using System.IO;
using BrimDeck.Core;

namespace BrimDeck;

// Owns one sidecar at a time. A process exit restarts collection with the same
// configuration; pause, reconfiguration and application exit cancel the retry.
internal sealed class PacerEngineConnection(Func<PacerEngineClient, Task> initialize) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private PacerEngineClient? current;
    private TaskCompletionSource? restart;
    private volatile bool ready, expanded, disposed;
    private Task? running;
    private string state = "starting";
    internal event Action<AiActivitySnapshot>? Snapshot;
    internal event Action<string>? StateChanged;

    internal void Start() => running ??= RunAsync();

    internal async Task SetExpandedAsync(bool value)
    {
        expanded = value;
        if (ready) await SendAsync("setExpanded", new { expanded = value }).ConfigureAwait(false);
    }

    internal async Task SendAsync(string method, object? parameters = null)
    {
        var client = current;
        if (!ready || client is null || disposed) throw new IOException("Activity engine is reconnecting.");
        try { await client.SendAsync(method, parameters).ConfigureAwait(false); }
        catch (PacerCommandException) { throw; }
        catch (PacerCommandTimeoutException) when (client.IsResponsive) { throw; }
        catch (IOException)
        {
            if (current == client) restart?.TrySetResult();
            throw;
        }
    }

    private void ChangeState(string value)
    {
        if (disposed || state == value) return;
        state = value; StateChanged?.Invoke(value);
    }

    private async Task RunAsync()
    {
        int failures = 0;
        while (!lifetime.IsCancellationRequested)
        {
            using var client = new PacerEngineClient();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            current = client; restart = ended;
            long started = Stopwatch.GetTimestamp();
            client.Stopped += () => ended.TrySetResult();
            client.Snapshot += snapshot =>
            {
                if (disposed || current != client) return;
                ChangeState("ready"); Snapshot?.Invoke(snapshot);
            };
            try
            {
                await initialize(client).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                ready = true;
                bool demand;
                do
                {
                    demand = expanded;
                    await client.SendAsync("setExpanded", new { expanded = demand }).ConfigureAwait(false);
                } while (demand != expanded && !lifetime.IsCancellationRequested);
                await ended.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException) { }
            finally
            {
                ready = false;
                Interlocked.CompareExchange(ref current, null, client);
                client.Dispose();
            }
            if (lifetime.IsCancellationRequested) break;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30)) failures = 0;
            ChangeState("reconnecting");
            try { await Task.Delay(RetryDelay(failures++), lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static TimeSpan RetryDelay(int failures) => TimeSpan.FromSeconds(failures switch
    { <= 0 => 1, 1 => 2, 2 => 4, 3 => 8, 4 => 15, _ => 30 });

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); current?.Dispose();
    }
}
