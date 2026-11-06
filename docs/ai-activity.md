# AI activity in BrimDeck

The AI page has **Usage** and **Live tasks** views. Usage keeps BrimDeck's existing account quotas, token totals and cost statistics. Live tasks uses the shared [Codex Pacer](https://github.com/RyanZhangNTU/codex-pacer) engine for local Codex, Claude Code and DeepSeek Harness sessions.

Task cards show the provider, project, model and reasoning effort; thinking/tool/waiting/completion state; turn duration; fresh output speed when available; first-token latency; subagent count; and DeepSeek turn costs. DeepSeek also has a local spending summary and seven-day chart. Provider filters and read acknowledgements operate on the engine's grouped task rows. Running compact totals keep the `N · rate t/s` format; missing or stale speed is `N · — t/s`, rather than a running label or an invented zero. The current engine protocol does not expose context-window occupancy, so this view does not infer a percentage from cumulative token totals.

The **AI quota rings** position switches to Codex Pacer's thinking, tool, reply or attention glyph while tasks are active, with its provider-colored rotating orbit. Idle restores the quota rings. Moving AI to the right also moves this state and the adjacent task count/speed to the right. Activity text prioritizes pending attention and failures, then running count and fresh aggregate speed, then unread completions; it has no tinted background or reserved trailing width and disappears when there is nothing to report. Task cards use the same glyph family. Motion follows **Animate status icons** in **Settings → Quotas and usage → Live AI tasks**, independently of expand/collapse animation, and respects Windows' reduced-motion setting. Hidden or unloaded icons release their clocks. This settings group also pauses collection, hides project/task names and reconnects the engine. The AI page's **···** button opens that settings page.

BrimDeck owns one engine child shared by all displays. An OS job closes its descendants when BrimDeck exits. Expanding a task view raises presentation demand; sleep/wake is forwarded to the engine. Failed initialization or a disconnected process clears live readings and reconnects with a bounded 1–30 second backoff, restoring configuration and expanded demand. Pause, reconfiguration and exit cancel pending retries. A slow command keeps an engine connected while fresh snapshots continue to arrive. Read acknowledgements include the displayed snapshot sequence so newer completions cannot be acknowledged accidentally.

The pinned engine patch carries modified rollout paths through its recursive Windows watch. A resumed conversation can be found in its original date directory even when absent from the bounded startup scan and SQLite index. At most 32 changed paths feed the existing 16-session discovery bound; history is not recursively rescanned. Unicode titles use Swift's whitespace and grapheme handling to avoid a Windows Foundation trimming crash.

Desktop paginated rollouts can append a writer UUID after the logical thread UUID. Discovery, local cursors and native subscription preflight consistently use the thread UUID, so derived conversations are separate tasks and the writer is not counted as another conversation. Copied ancestor headers still cannot rename a cursor or create inherited activity. Native preflight can locate an old unindexed UUIDv7 thread in its original date folder, then applies the same metadata and live-writer checks before subscribing.

## Data and scope

The engine runs with `--read-only-diagnostics`. It reads local provider session logs and supported local Desktop observations, but does not install Claude hooks, change provider settings, open browser logins, fetch additional quotas, or write Codex Pacer's completion state. Codex Desktop observations initialize a separate observer connection and subscribe to existing task streams; they do not create or resume turns. The BrimDeck transport allows only initialization/configuration, presentation demand, refresh, read acknowledgements, sleep/wake and shutdown. It rejects navigation, host activation and provider setup before writing a command to the engine. Its private process job owns only the launched engine and its descendants; it does not attach the existing Codex application. Observation still consumes local CPU, file reads and IPC traffic. Existing Claude hooks/telemetry can provide richer attention observations. DeepSeek activity requires DeepSeek Harness session logs; the DeepSeek website alone does not provide these local tasks. Newly created source roots may take the engine's next discovery pass to appear.

Account quotas and balances continue through BrimDeck's existing provider configuration, including its DeepSeek / DeepSeek Harness sources. Task monitoring does not reuse an API key from BrimDeck. Remote SSH/WSL discovery, Claude cloud setup, browser bridges, automatic hook installation and conversation navigation are outside this local activity integration. The separate Codex Pacer UI need not run. Task titles stay in process memory; protocol bodies and engine stderr are never written to BrimDeck logs.

## Building and packaging

The engine revision and protocol are pinned in [pacer-engine.lock.json](../scripts/pacer-engine.lock.json). From a Windows x64 developer shell with Visual Studio C++ tools, a Windows SDK, Git, Python and .NET 10:

```powershell
./scripts/Build-PacerEngine.ps1
dotnet build src/BrimDeck/BrimDeck.csproj -c Release -p:Platform=x64
dotnet run --project tests/BrimDeck.ActivityTests -c Release -- .artifacts/pacer-engine/PacerEngine.exe
dotnet run --project tests/BrimDeck.Tests -c Release
```

The script checks out the pinned public source into an ignored cache, applies the checksum-verified [BrimDeck patch](../scripts/pacer-engine.patch) in a separate checkout, installs its verified Swift toolchain if `PACER_SWIFT_ROOT` is not supplied, and builds outside the working tree. `PACER_SWIFT_ROOT`, `PACER_DOTNET_ROOT`, `PACER_WINDOWS_SDK_ROOT` and `PACER_WINDOWS_SCRATCH_PATH` can reuse existing tools and build caches. `-Products <directory>` accepts Pacer's prebuilt engine products only when their source fingerprint, architecture and configuration match the patched source exactly. The build includes the runtime, resource bundles, source receipt, MIT notice and Swift licenses under `.artifacts/pacer-engine`. MSBuild copies this directory to `Engine` for builds and publishes; `Publish-Release.ps1` prepares it before packaging. A UI-only build without this directory still supports quotas and explicitly reports the missing task engine.

Integration tests use invented demo data and isolated temporary provider homes. They exercise the real protocol, all three local readers, appended unread completions, unindexed old conversations with full discovery slots, paginated branch identity and independent lifecycle/tool state, Unicode title boundaries, privacy, sleep/wake and shutdown. A process fixture covers initialization failure, later exits, restored settings, a slow reply with healthy snapshots, cancelling reconnect, rejecting control commands and preserving an unrelated running process on disposal. Render the actual WPF controls with the checked-in synthetic engine fixture:

```powershell
dotnet run --project scripts/readme-media -c Release -p:Platform=x64 -- artifacts/ai-activity zh-CN ai-activity
```

The fixture was captured from the pinned engine's offline reference demo, not from user conversations. The scene checks light/dark themes, both supported panel scales, provider filters, the quota page, thinking/tool/reply transitions, text spacing, idle quota restoration, reduced motion and hidden/unloaded animation cleanup. Engine changes require updating the lock, revalidating the real protocol/readers and refreshing this fixture. Codex Pacer is MIT licensed; BrimDeck retains its Apache 2.0 license and ships the engine notices.

The additional `ai-width` scene checks live task-count and speed-width transitions at both panel scales, three capsule heights and both WPF text-formatting modes. Auto width measures activity text in the window's inherited layout context, rounds the complete row up to device pixels and applies sub-DIP width changes instead of discarding them.
