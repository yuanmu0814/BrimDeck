using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using BrimDeck.Core;

namespace BrimDeck;

// A private child of BrimDeck, not the running Codex Pacer application. No task
// bodies or stderr are logged. The read-only engine leaves provider settings,
// hooks, credentials and Codex Pacer's saved state untouched.
internal sealed class PacerEngineClient : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writer = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> pending = new();
    private readonly TaskCompletionSource hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? child;
    private PacerJob? job;
    private Task? reading, errors;
    private long request;
    private ulong sequence;
    private bool disposed;
    internal event Action<AiActivitySnapshot>? Snapshot;
    internal event Action? Stopped;

    internal async Task StartAsync(string executable, string language, bool hideTitles, int retentionMinutes, bool demo = false,
        string? codexHome = null, string? claudeHome = null, string? deepseekHome = null)
    {
        if (child is not null || disposed) throw new InvalidOperationException("Activity engine already started.");
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        start.ArgumentList.Add("--read-only-diagnostics");
        if (demo) { start.ArgumentList.Add("--demo"); start.ArgumentList.Add("--reference-fixture"); }
        start.ArgumentList.Add("--language"); start.ArgumentList.Add(language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en");
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var zone))
        { start.ArgumentList.Add("--time-zone"); start.ArgumentList.Add(zone); }
        else
        { start.ArgumentList.Add("--time-zone-offset"); start.ArgumentList.Add(((int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        job = new PacerJob();
        try
        {
            child = Process.Start(start) ?? throw new IOException("Could not start activity engine.");
            job.Attach(child);
            reading = ReadAsync(child.StandardOutput.BaseStream);
            errors = DrainErrorsAsync(child.StandardError.BaseStream);
            await hello.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
            await SendAsync("initialize", new
            {
                enabledProviders = AiActivity.Providers, hideProjects = hideTitles, retentionMinutes = Math.Clamp(retentionMinutes, 0, 10080),
                showEmbeddedCodexSessions = true, showEmbeddedClaudeSessions = true, showClaudeCloudProjects = false,
                monitorSSH = false, monitorWSL = false, monitorRemoteControl = false,
                codexHome, claudeHome, deepseekHome
            }).ConfigureAwait(false);
        }
        catch { Dispose(); throw; }
    }

    internal async Task SendAsync(string method, object? parameters = null)
    {
        if (disposed || child is not { HasExited: false } process) throw new IOException("Activity engine is stopped.");
        string id = Interlocked.Increment(ref request).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, id, method, @params = parameters });
        if (frame.Length > 64 * 1024) throw new InvalidDataException("Activity command is too large.");
        pending[id] = reply;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(new byte[] { 10 }, timeout.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { writer.Release(); }
            await reply.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw new IOException("Activity command timed out or was cancelled."); }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task ReadAsync(Stream stream)
    {
        byte[] buffer = new byte[8192];
        using var line = new MemoryStream();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] == 10)
                    {
                        if (line.Length > 0) Receive(line.ToArray());
                        line.SetLength(0);
                    }
                    else
                    {
                        if (line.Length >= 1024 * 1024) throw new InvalidDataException("Activity frame exceeds the protocol limit.");
                        line.WriteByte(buffer[i]);
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or FormatException) { }
        finally
        {
            hello.TrySetException(new IOException("Activity engine disconnected."));
            foreach (var value in pending.Values) value.TrySetException(new IOException("Activity engine disconnected."));
            job?.Dispose();
            if (!disposed) Stopped?.Invoke();
        }
    }

    private void Receive(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var frame = document.RootElement;
        if (frame.GetProperty("protocolVersion").GetInt32() != 1) throw new InvalidDataException("Activity protocol mismatch.");
        switch (frame.GetProperty("type").GetString())
        {
            case "hello":
                if (frame.GetProperty("engine").GetString() != "PacerEngine") throw new InvalidDataException("Unexpected activity engine.");
                hello.TrySetResult(); break;
            case "snapshot":
                if (!hello.Task.IsCompletedSuccessfully) throw new InvalidDataException("Activity snapshot before handshake.");
                var value = AiActivitySnapshot.Parse(bytes);
                if (value.Sequence <= sequence) throw new InvalidDataException("Out-of-order activity snapshot.");
                sequence = value.Sequence; Snapshot?.Invoke(value); break;
            case "result":
                if (frame.GetProperty("ok").GetBoolean() && frame.GetProperty("id").GetString() is { } id && pending.TryGetValue(id, out var reply)) reply.TrySetResult();
                break;
            case "error":
                if (frame.TryGetProperty("id", out var errorId) && errorId.GetString() is { } failed && pending.TryGetValue(failed, out var failure))
                    failure.TrySetException(new PacerCommandException(frame.TryGetProperty("code", out var code) ? code.GetString() ?? "engineError" : "engineError"));
                break;
            default: throw new InvalidDataException("Unexpected activity message.");
        }
    }

    private async Task DrainErrorsAsync(Stream stream)
    {
        byte[] buffer = new byte[4096];
        try { while (await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false) > 0) { } }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); job?.Dispose();
        try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        child?.Dispose();
    }
}

internal sealed class PacerCommandException(string code) : IOException("Activity command rejected.")
{ internal string Code { get; } = code; }
