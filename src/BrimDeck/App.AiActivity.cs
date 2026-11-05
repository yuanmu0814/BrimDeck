using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck;

public partial class App
{
    private PacerEngineClient? _activityEngine;
    internal AiActivitySnapshot Activity { get; private set; } = AiActivitySnapshot.Empty;
    internal string ActivityState { get; private set; } = "starting";
    internal string? ActivityActionMessage { get; private set; }
    private string _activityConfiguration = "uninitialized";
    private readonly HashSet<MainWindow> _activityPresenting = [];
    internal void PresentActivity(MainWindow deck, bool present)
    {
        bool before = _activityPresenting.Count > 0;
        if (present) _activityPresenting.Add(deck); else _activityPresenting.Remove(deck);
        bool after = _activityPresenting.Count > 0;
        if (before != after) _ = ActivityCommandAsync("setExpanded", new { expanded = after });
    }

    private void ApplyActivitySettings()
    {
        bool wanted = Settings.AiActivityEnabled && (Settings.UsagePage || Settings.ShowsSummary(Settings.Style));
        string configuration = wanted ? $"{Loc.Language}|{Settings.AiHideTaskTitles}|{Settings.AiRetentionMinutes}" : "";
        if (configuration == _activityConfiguration) return;
        _activityConfiguration = configuration;
        _activityEngine?.Dispose(); _activityEngine = null;
        Activity = AiActivitySnapshot.Empty;
        ActivityState = wanted ? "starting" : "paused";
        PublishActivity();
        if (wanted) _ = StartActivityAsync();
    }

    internal void RetryActivity()
    { _activityConfiguration = ""; ApplyActivitySettings(); }

    private async Task StartActivityAsync()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "Engine", "PacerEngine.exe");
        if (!File.Exists(executable)) { ActivityState = "missing"; PublishActivity(); return; }
        var engine = new PacerEngineClient(); _activityEngine = engine;
        engine.Snapshot += snapshot => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Exiting || _activityEngine != engine) return;
            Activity = snapshot; ActivityState = "ready"; PublishActivity();
        });
        engine.Stopped += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Exiting || _activityEngine != engine) return;
            Activity = AiActivitySnapshot.Empty; ActivityState = "stopped"; PublishActivity();
        });
        try
        {
            await engine.StartAsync(executable, Loc.Language, Settings.AiHideTaskTitles, Settings.AiRetentionMinutes);
            if (_activityEngine == engine && _activityPresenting.Count > 0) await engine.SendAsync("setExpanded", new { expanded = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            if (Exiting || _activityEngine != engine) return;
            engine.Dispose(); Activity = AiActivitySnapshot.Empty; ActivityState = "stopped"; PublishActivity();
        }
    }

    private void PublishActivity()
    {
        if (Deck is null) return;
        foreach (var deck in Decks) deck.ActivityChanged();
    }

    internal async Task ActivityCommandAsync(string method, object? parameters = null)
    {
        if (_activityEngine is not { } engine) return;
        try { await engine.SendAsync(method, parameters); ActivityActionMessage = null; }
        catch (PacerCommandException error)
        {
            ActivityActionMessage = error.Code == "staleSnapshot" ? Loc.T("任务已更新，请再次点击。", "Tasks changed. Please try again.")
                : Loc.T("此操作当前不可用。", "This action is currently unavailable.");
            if (!Exiting) PublishActivity();
        }
        catch (IOException)
        {
            if (!Exiting && engine == _activityEngine)
            {
                engine.Dispose(); _activityEngine = null; Activity = AiActivitySnapshot.Empty;
                ActivityState = "stopped"; PublishActivity();
            }
        }
    }

    private void ActivityPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        string? method = e.Mode == PowerModes.Suspend ? "prepareForSleep" : e.Mode == PowerModes.Resume ? "resumeAfterSleep" : null;
        if (method is not null && !Exiting) Dispatcher.BeginInvoke(() => _ = ActivityCommandAsync(method));
    }
}
