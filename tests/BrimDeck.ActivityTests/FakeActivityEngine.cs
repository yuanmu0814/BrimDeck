using System.Text.Json;

// This executable doubles as a private protocol fixture. It touches only the
// explicitly supplied synthetic home; no actual provider collectors run.
internal static class FakeActivityEngine
{
    internal static async Task Run()
    {
        var output = new SemaphoreSlim(1);
        ulong sequence = 0;
        async Task Write(object value)
        {
            await output.WaitAsync();
            try { Console.WriteLine(JsonSerializer.Serialize(value)); }
            finally { output.Release(); }
        }
        async Task Snapshot() => await Write(new
        {
            protocolVersion = 1, type = "snapshot", sequence = ++sequence, capturedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            tasks = Array.Empty<object>(), requests = Array.Empty<object>(), sources = Array.Empty<object>(),
            presentation = new { rows = Array.Empty<object>(), header = new { activeCount = 0, orbit = new Dictionary<string, int>() } }
        });
        await Write(new { protocolVersion = 1, type = "hello", engine = "PacerEngine" });
        string? home = null;
        int attempt = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var frame = JsonDocument.Parse(line);
            string id = frame.RootElement.GetProperty("id").GetString()!;
            string? method = frame.RootElement.GetProperty("method").GetString();
            if (method == "initialize")
            {
                var parameters = frame.RootElement.GetProperty("params");
                home = Path.GetFullPath(parameters.GetProperty("codexHome").GetString()!);
                if (!home.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                    || !home.Contains("BrimDeck-Activity-", StringComparison.Ordinal)) return;
                string counter = Path.Combine(home, "attempts.txt");
                attempt = File.Exists(counter) ? int.Parse(await File.ReadAllTextAsync(counter)) + 1 : 1;
                await File.WriteAllTextAsync(counter, attempt.ToString());
                if (attempt == 1) return; // Exit during initialization.
                if (!parameters.GetProperty("hideProjects").GetBoolean() || parameters.GetProperty("retentionMinutes").GetInt32() != 120
                    || parameters.GetProperty("enabledProviders").GetArrayLength() != 3) return;
            }
            if (home is not null) await File.AppendAllTextAsync(Path.Combine(home, "commands.txt"), method + "\n");
            if (method == "prepareForSleep") return; // Fail an outstanding request.
            if (method == "refresh")
            {
                // Healthy updates continue while one reply is deliberately late.
                for (int count = 0; count < 32; count++) { await Snapshot(); await Task.Delay(500); }
            }
            await Write(new { protocolVersion = 1, type = "result", id, ok = true });
            if (method == "initialize")
            {
                await Snapshot();
                if (attempt == 2) { await Task.Delay(100); return; }
            }
            if (method == "setExpanded" && home is not null && frame.RootElement.GetProperty("params").GetProperty("expanded").GetBoolean())
                await File.WriteAllTextAsync(Path.Combine(home, "expanded.txt"), "true");
        }
    }
}
