using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Core;

string enginePath = Path.GetFullPath(args.Length > 0 ? args[0] : ".artifacts/pacer-engine/PacerEngine.exe");
int checks = 0;
void Check(string name, bool condition)
{ if (!condition) throw new InvalidOperationException("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; }
var snapshots = new ConcurrentQueue<AiActivitySnapshot>();
using (var engine = new PacerEngineClient())
{
    engine.Snapshot += snapshots.Enqueue;
    await engine.StartAsync(enginePath, "zh-CN", false, 60, demo: true);
    async Task<AiActivitySnapshot> Wait(Func<AiActivitySnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var snapshot in snapshots.Reverse()) if (predicate(snapshot)) return snapshot;
            await Task.Delay(25);
        }
        throw new TimeoutException("The engine did not publish the expected synthetic snapshot.");
    }
    var first = await Wait(snapshot => snapshot.Tasks.Count > 0);
    Check("real engine handshake and bounded snapshot decode", first.ProtocolVersion == 1 && first.Sequence > 0 && first.Presentation is not null);
    Check("all three providers are enabled", first.Tasks.Select(task => task.Provider).Distinct().Count() == 3);
    Check("grouped task rows and model metadata arrive", first.Presentation!.Rows.Count > 0 && first.Presentation.Rows.All(row => !string.IsNullOrWhiteSpace(row.Model)));
    await engine.SendAsync("setExpanded", new { expanded = true });
    await engine.SendAsync("refresh");
    await engine.SendAsync("markAllRead", new { snapshotSequence = snapshots.Last().Sequence });
    Check("expanded demand, refresh and read acknowledgement", true);
    await engine.SendAsync("prepareForSleep");
    await engine.SendAsync("resumeAfterSleep");
    Check("sleep and wake command lifecycle", true);
    await engine.SendAsync("configure", new { hideProjects = true });
    var privateSnapshot = await Wait(snapshot => snapshot.Sequence > first.Sequence && snapshot.Tasks.All(task => !first.Tasks.Any(old => old.Title == task.Title)));
    Check("privacy preference reaches shared engine", privateSnapshot.Tasks.Count > 0);
    await engine.SendAsync("configure", new { hideProjects = false });
    var capture = await Wait(snapshot => snapshot.Sequence > privateSnapshot.Sequence && snapshot.Tasks.Count > 0 && snapshot.Tasks.Any(task => first.Tasks.Any(old => old.Title == task.Title)));
    if (args.Length > 1)
    {
        string destination = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(capture));
    }
    Check("snapshots remain ordered", snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
}
// A second launch reads only isolated empty homes, exercising the live collectors
// without opening real accounts or installing provider hooks.
string root = Path.Combine(Path.GetTempPath(), "BrimDeck-Activity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var sources = AiActivity.Providers.Select(provider => Path.Combine(root, provider)).ToArray();
    foreach (string source in sources) Directory.CreateDirectory(source);
    Directory.CreateDirectory(Path.Combine(sources[2], "sessions"));
    using var engine = new PacerEngineClient();
    var ready = new TaskCompletionSource<AiActivitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    engine.Snapshot += value => ready.TrySetResult(value);
    await engine.StartAsync(enginePath, "en-US", false, 60, codexHome: sources[0], claudeHome: sources[1], deepseekHome: sources[2]);
    var empty = await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
    Check("live collectors with isolated homes do not invent tasks", empty.Tasks.Count == 0 && empty.RunningCount == 0);
    Check("read-only collectors leave source directories untouched", sources.Take(2).All(source => !Directory.EnumerateFileSystemEntries(source).Any())
        && Directory.EnumerateFileSystemEntries(sources[2]).Count() == 1 && !Directory.EnumerateFileSystemEntries(Path.Combine(sources[2], "sessions")).Any());
    var observed = new ConcurrentQueue<AiActivitySnapshot>();
    engine.Snapshot += observed.Enqueue;
    await engine.SendAsync("setExpanded", new { expanded = true });
    string Row(object value) => JsonSerializer.Serialize(value) + "\n";
    async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (!predicate()) throw new TimeoutException("Local session watch did not publish the expected state.");
    }
    foreach (string provider in AiActivity.Providers)
    {
        string id = Guid.NewGuid().ToString("D"), turn = Guid.NewGuid().ToString("D"), assistant = Guid.NewGuid().ToString("D");
        string stamp = DateTimeOffset.UtcNow.ToString("O"); long milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string file = provider switch
        {
            "codex" => Path.Combine(sources[0], "sessions", DateTime.Now.ToString("yyyy/MM/dd"), "rollout-" + id + ".jsonl"),
            "claude" => Path.Combine(sources[1], "projects", "C--Synthetic", id + ".jsonl"),
            _ => Path.Combine(sources[2], "sessions", "C--Synthetic", "session-" + id, "session.v4.jsonl.zstd")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string initial, ending;
        if (provider == "codex")
        {
            initial = Row(new { type = "session_meta", timestamp = stamp, payload = new { id, source = "cli", cwd = "C:/Synthetic/Activity" } })
                + Row(new { type = "event_msg", timestamp = stamp, payload = new { type = "task_started", turn_id = turn } });
            ending = Row(new { type = "event_msg", timestamp = stamp, payload = new { type = "task_complete", turn_id = turn } });
        }
        else if (provider == "claude")
        {
            initial = Row(new { type = "user", sessionId = id, uuid = turn, timestamp = stamp, origin = new { kind = "human" }, message = new { role = "user", content = "synthetic-private-prompt" } });
            ending = Row(new { type = "assistant", sessionId = id, uuid = assistant, parentUuid = turn, timestamp = stamp,
                message = new { role = "assistant", content = new[] { new { type = "text", text = "synthetic-private-reply" } }, stop_reason = "end_turn" } })
                + Row(new { type = "system", sessionId = id, uuid = Guid.NewGuid().ToString("D"), parentUuid = assistant, timestamp = stamp,
                    subtype = "stop_hook_summary", preventedContinuation = false, hookErrors = Array.Empty<string>(), hookAdditionalContext = Array.Empty<string>() });
        }
        else
        {
            initial = Row(new { type = "session", version = 4, id = "session-" + id, createdAt = milliseconds, cwd = "C:/Synthetic/Activity", delegationDepth = 0 })
                + Row(new { type = "turn/start", seq = 1, time = milliseconds + 1, data = new { turn = 1 } });
            ending = Row(new { type = "turn/start", seq = 2, time = milliseconds + 2, data = new { turn = 2 } })
                + Row(new { type = "turn/end", seq = 3, time = milliseconds + 3, data = new { turn = 2, reason = new { kind = "completed" } } });
        }
        byte[] Encode(string value)
        {
            byte[] content = Encoding.UTF8.GetBytes(value);
            if (provider != "deepseek") return content;
            byte[] frame = new byte[12 + content.Length];
            frame[0] = 0x28; frame[1] = 0xb5; frame[2] = 0x2f; frame[3] = 0xfd; frame[4] = 0xa0;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), (uint)content.Length);
            int block = (content.Length << 3) | 1; frame[9] = (byte)block; frame[10] = (byte)(block >> 8); frame[11] = (byte)(block >> 16);
            content.CopyTo(frame, 12); return frame;
        }
        await File.WriteAllBytesAsync(file, Encode(initial));
        await Until(() => observed.Any(snapshot => snapshot.Tasks.Any(task => task.Provider == provider && task.SessionId == id)));
        Check(provider + " local log is discovered without manual refresh", true);
        await using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) await stream.WriteAsync(Encode(ending));
        await Until(() => observed.Any(snapshot => snapshot.Tasks.Any(task => task.Provider == provider && task.SessionId == id && task.Phase == "completed" && task.Unread)));
        Check(provider + " appended completion becomes unread", true);
        Check(provider + " protocol omits conversation contents", !observed.Any(snapshot => JsonSerializer.Serialize(snapshot).Contains("synthetic-private-", StringComparison.Ordinal)));
    }
}
finally
{
    string resolved = Path.GetFullPath(root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("BrimDeck-Activity-", StringComparison.Ordinal) || new DirectoryInfo(resolved).LinkTarget is not null)
        throw new InvalidOperationException("Unexpected activity fixture path.");
    Directory.Delete(resolved, true);
}
Console.WriteLine($"All {checks} activity integration checks passed.");
