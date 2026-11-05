using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BrimDeck;
using BrimDeck.Core;

if (args.Contains("--read-only-diagnostics")) { await FakeActivityEngine.Run(); return; }
string enginePath = Path.GetFullPath(args.Length > 0 ? args[0] : ".artifacts/pacer-engine/PacerEngine.exe");
if (args.Contains("--diagnose-real"))
{
    using var probe = new PacerEngineClient();
    int frames = 0, maxTasks = 0, stopped = 0;
    probe.Snapshot += value => { Interlocked.Increment(ref frames); maxTasks = Math.Max(maxTasks, value.Tasks.Count); };
    probe.Stopped += () => Interlocked.Increment(ref stopped);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    bool initialized = false;
    try { await probe.StartAsync(enginePath, "zh-CN", false, 60); initialized = true; }
    catch (Exception error) { Console.WriteLine("startFailure=" + error.GetType().Name); }
    double initializeSeconds = clock.Elapsed.TotalSeconds;
    int seconds = args.Length > 2 && int.TryParse(args[2], out int requested) ? Math.Clamp(requested, 20, 300) : 25;
    if (initialized) await Task.Delay(TimeSpan.FromSeconds(seconds));
    Console.WriteLine(JsonSerializer.Serialize(new { initialized, initializeSeconds, frames, maxTasks, stopped, probe.FailureCode, probe.FailureField, probe.ExitCode }));
    return;
}
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
    engine.Dispose();

    string resumedHome = Path.Combine(root, "resume-codex"), oldId = Guid.NewGuid().ToString("D");
    string oldFile = Path.Combine(resumedHome, "sessions", DateTime.Now.AddMonths(-3).ToString("yyyy/MM/dd"), "rollout-" + oldId + ".jsonl");
    Directory.CreateDirectory(Path.GetDirectoryName(oldFile)!);
    string oldStamp = DateTimeOffset.UtcNow.AddMonths(-3).ToString("O");
    await File.WriteAllTextAsync(oldFile, Row(new { type = "session_meta", timestamp = oldStamp, payload = new { id = oldId, source = "cli", cwd = "C:/Synthetic/Resumed" } })
        + Row(new { type = "event_msg", timestamp = oldStamp, payload = new { type = "task_complete", turn_id = Guid.NewGuid().ToString("D") } }));
    File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddMonths(-3));
    // Fill the bounded recent-history slots before starting the engine. The
    // old rollout has no SQLite index entry and is outside both date folders.
    string today = Path.Combine(resumedHome, "sessions", DateTime.Now.ToString("yyyy/MM/dd"));
    Directory.CreateDirectory(today);
    for (int index = 0; index < 20; index++)
    {
        string historyId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(today, "rollout-" + historyId + ".jsonl"), Row(new { type = "session_meta", timestamp = oldStamp, payload = new { id = historyId, source = "cli" } })
            + Row(new { type = "event_msg", timestamp = oldStamp, payload = new { type = "task_complete", turn_id = Guid.NewGuid().ToString("D") } }));
    }
    using (var resumed = new PacerEngineClient())
    {
        var history = new ConcurrentQueue<AiActivitySnapshot>();
        resumed.Snapshot += history.Enqueue;
        await resumed.StartAsync(enginePath, "en-US", false, 60, codexHome: resumedHome, claudeHome: sources[1], deepseekHome: sources[2]);
        await resumed.SendAsync("setExpanded", new { expanded = true });
        Check("old unindexed rollout is absent from bounded startup discovery", !history.Any(snapshot => snapshot.Tasks.Any(task => task.SessionId == oldId)));
        string activeTurn = Guid.NewGuid().ToString("D");
        await File.AppendAllTextAsync(oldFile, Row(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow.ToString("O"), payload = new { type = "task_started", turn_id = activeTurn } }));
        await Until(() => history.Any(snapshot => snapshot.Tasks.Any(task => task.SessionId == oldId && task.TurnId == activeTurn && task.Phase == "running")));
        Check("three-month-old resumed conversation is discovered without refresh or index", true);
        await File.AppendAllTextAsync(oldFile, Row(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow.ToString("O"), payload = new { type = "task_complete", turn_id = activeTurn } }));
        await Until(() => history.Any(snapshot => snapshot.Tasks.Any(task => task.SessionId == oldId && task.TurnId == activeTurn && task.Phase == "completed" && task.Unread)));
        Check("resumed old conversation completion remains tracked", true);
        string recoveredId = Guid.NewGuid().ToString("D"), recoveredTurn = Guid.NewGuid().ToString("D");
        string recoveredFile = Path.Combine(Path.GetDirectoryName(oldFile)!, "rollout-" + recoveredId + ".jsonl");
        await File.WriteAllTextAsync(recoveredFile, Row(new { type = "session_meta", timestamp = DateTimeOffset.UtcNow.ToString("O"), payload = new { id = recoveredId, source = "cli" } })
            + Row(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow.ToString("O"), payload = new { type = "task_started", turn_id = recoveredTurn } }));
        await Until(() => history.Any(snapshot => snapshot.Tasks.Any(task => task.SessionId == recoveredId && task.Phase == "running")));
        Check("structural events also preserve rollout paths outside recent date folders", true);
    }

    string titlesHome = Path.Combine(root, "titles-codex");
    string[] titles = [" \t\n\u3000" + new string('汉', 250) + "🧑‍💻 \u3000", "\u2003🧑‍💻 Chat\u3000", " \t\n\u3000"];
    var indexed = new List<(string File, string Title)>();
    var titleIds = new List<string>();
    foreach (string title in titles)
    {
        string id = Guid.NewGuid().ToString("D"), turn = Guid.NewGuid().ToString("D");
        string file = Path.Combine(titlesHome, "sessions", "2020", "01", "01", "rollout-" + id + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, Row(new { type = "session_meta", timestamp = DateTimeOffset.UtcNow.ToString("O"), payload = new { id, source = "cli", cwd = "C:/Synthetic/Titles" } })
            + Row(new { type = "event_msg", timestamp = DateTimeOffset.UtcNow.ToString("O"), payload = new { type = "task_started", turn_id = turn } }));
        indexed.Add((file, title)); titleIds.Add(id);
    }
    FixtureIndex.Create(Path.Combine(titlesHome, "state_1.sqlite"), indexed);
    using (var titleEngine = new PacerEngineClient())
    {
        var titleSnapshots = new ConcurrentQueue<AiActivitySnapshot>();
        titleEngine.Snapshot += titleSnapshots.Enqueue;
        await titleEngine.StartAsync(enginePath, "en-US", false, 60, codexHome: titlesHome, claudeHome: sources[1], deepseekHome: sources[2]);
        await Until(() => titleSnapshots.Any(snapshot => titleIds.All(id => snapshot.Tasks.Any(task => task.SessionId == id))));
        var snapshot = titleSnapshots.Last(value => titleIds.All(id => value.Tasks.Any(task => task.SessionId == id)));
        Check("Unicode title trimming preserves graphemes and the 240-character bound", snapshot.Tasks.Single(task => task.SessionId == titleIds[0]).Title == new string('汉', 240)
            && snapshot.Tasks.Single(task => task.SessionId == titleIds[1]).Title == "🧑‍💻 Chat");
        Check("empty Unicode title falls back safely", !string.IsNullOrWhiteSpace(snapshot.Tasks.Single(task => task.SessionId == titleIds[2]).Title));
    }

    string reconnectHome = Path.Combine(root, "reconnect"); Directory.CreateDirectory(reconnectHome);
    string fakeEngine = Environment.ProcessPath!;
    using (var connection = new PacerEngineConnection(client => client.StartAsync(fakeEngine, "en-US", true, 120, codexHome: reconnectHome)))
    {
        var states = new ConcurrentQueue<string>();
        connection.StateChanged += states.Enqueue;
        await connection.SetExpandedAsync(true);
        connection.Start();
        string countFile = Path.Combine(reconnectHome, "attempts.txt"), expandedFile = Path.Combine(reconnectHome, "expanded.txt");
        string ReadFixture(string file)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) { return ""; } // The fixture may be updating this status.
        }
        int Attempts() => int.TryParse(ReadFixture(countFile), out int count) ? count : 0;
        await Until(() => Attempts() >= 3 && ReadFixture(expandedFile) == "true");
        Check("initialization exit and later crash both reconnect automatically", Attempts() == 3 && states.Count(state => state == "reconnecting") >= 2);
        Check("reconnect reapplies provider, privacy, retention and expanded demand", ReadFixture(expandedFile) == "true");
        var busy = connection.SendAsync("testBusy");
        bool timedOut = false;
        try { await busy; } catch (PacerCommandTimeoutException) { timedOut = true; }
        Check("slow command keeps a live snapshot stream connected", timedOut && Attempts() == 3 && states.Last() == "ready");
        try { await connection.SendAsync("testDrop"); } catch (IOException) { }
        await Until(() => states.Last() == "reconnecting");
        connection.Dispose();
        int pausedAttempts = Attempts();
        await Task.Delay(PacerEngineConnection.RetryDelay(2) + TimeSpan.FromMilliseconds(300));
        Check("pause cancels a scheduled reconnect", Attempts() == pausedAttempts);
        Check("reconnect backoff is bounded", PacerEngineConnection.RetryDelay(0).TotalSeconds == 1 && PacerEngineConnection.RetryDelay(100).TotalSeconds == 30);
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
