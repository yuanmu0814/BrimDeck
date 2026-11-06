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
    private readonly object processGate = new();
    private readonly SemaphoreSlim writer = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> pending = new();
    private readonly TaskCompletionSource hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? child;
    private PacerJob? job;
    private Task? reading, errors;
    private long request;
    private ulong sequence;
    private bool disposed;
    private long lastSnapshot;
    internal bool IsResponsive => Volatile.Read(ref lastSnapshot) is var stamp && stamp != 0 && Stopwatch.GetElapsedTime(stamp) < TimeSpan.FromSeconds(20);
    internal string? FailureCode { get; private set; }
    internal string? FailureField { get; private set; }
    internal int? ExitCode { get; private set; }
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
        try
        {
            lock (processGate)
            {
                if (disposed) throw new IOException("Activity engine stopped.");
                job = new PacerJob();
                child = Process.Start(start) ?? throw new IOException("Could not start activity engine.");
                job.Attach(child);
                reading = ReadAsync(child.StandardOutput.BaseStream);
                errors = DrainErrorsAsync(child.StandardError.BaseStream);
            }
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
        // BrimDeck observes activity only. Keep Pacer's navigation, host
        // activation and provider-setup commands outside this channel.
        if (method is not ("initialize" or "configure" or "setExpanded" or "refresh" or "markRead" or "markAllRead"
            or "prepareForSleep" or "resumeAfterSleep" or "shutdown"))
            throw new PacerCommandException("unsupportedObserverCommand");
        Process process;
        lock (processGate)
        {
            if (disposed || child is not { HasExited: false }) throw new IOException("Activity engine is stopped.");
            process = child;
        }
        string id = Interlocked.Increment(ref request).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, id, method, @params = parameters });
        if (frame.Length > 64 * 1024) throw new InvalidDataException("Activity command is too large.");
        pending[id] = reply;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(method == "initialize" ? 45 : 15));
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
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { throw new PacerCommandTimeoutException(); }
        catch (OperationCanceledException) { throw new IOException("Activity engine stopped."); }
        catch (ObjectDisposedException) { throw new IOException("Activity engine stopped."); }
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
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or FormatException)
        {
            FailureCode = error.GetType().Name;
            if (error is JsonException json && json.Path is { Length: <= 128 } path && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '$' or '.' or '[' or ']')) FailureField = path;
        }
        finally
        {
            try { if (child is { HasExited: true } process) ExitCode = process.ExitCode; }
            catch (InvalidOperationException) { }
            FailureCode ??= "endOfStream";
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
                sequence = value.Sequence; Volatile.Write(ref lastSnapshot, Stopwatch.GetTimestamp()); Snapshot?.Invoke(value); break;
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
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (processGate)
        {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); job?.Dispose();
            try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            child?.Dispose();
        }
    }
}

internal sealed class PacerCommandException(string code) : IOException("Activity command rejected.")
{ internal string Code { get; } = code; }

internal sealed class PacerCommandTimeoutException() : IOException("Activity engine is busy.");
