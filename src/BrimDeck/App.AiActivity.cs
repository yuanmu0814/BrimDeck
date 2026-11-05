using System.IO;
using System.Windows.Threading;
using BrimDeck.Core;
using Microsoft.Win32;

namespace BrimDeck;

public partial class App
{
    private PacerEngineConnection? _activityEngine;
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
        if (before != after) _ = SetActivityExpandedAsync(after);
    }

    private void ApplyActivitySettings()
    {
        bool wanted = Settings.AiActivityEnabled && (Settings.UsagePage || Settings.ShowsSummary(Settings.Style));
        string configuration = wanted ? $"{Loc.Language}|{Settings.AiHideTaskTitles}|{Settings.AiRetentionMinutes}" : "";
        if (configuration == _activityConfiguration) return;
        _activityConfiguration = configuration;
        _activityEngine?.Dispose(); _activityEngine = null;
        ActivityActionMessage = null;
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
        string language = Loc.Language;
        bool hideTitles = Settings.AiHideTaskTitles;
        int retention = Settings.AiRetentionMinutes;
        var engine = new PacerEngineConnection(client => client.StartAsync(executable, language, hideTitles, retention));
        _activityEngine = engine;
        engine.Snapshot += snapshot => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Exiting || _activityEngine != engine) return;
            Activity = snapshot; ActivityState = "ready"; ActivityActionMessage = null; PublishActivity();
        });
        engine.StateChanged += state => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (Exiting || _activityEngine != engine) return;
            if (state == "reconnecting") Activity = AiActivitySnapshot.Empty;
            ActivityState = state; PublishActivity();
        });
        try
        {
            await engine.SetExpandedAsync(_activityPresenting.Count > 0);
            engine.Start();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            if (Exiting || _activityEngine != engine) return;
            engine.Dispose(); Activity = AiActivitySnapshot.Empty; ActivityState = "stopped"; PublishActivity();
        }
    }

    private async Task SetActivityExpandedAsync(bool expanded)
    {
        if (_activityEngine is not { } engine) return;
        try { await engine.SetExpandedAsync(expanded); }
        catch (IOException) { } // The connection supervises transport failures.
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
                ActivityActionMessage = Loc.T("引擎忙碌或正在重连，请稍后重试。", "The engine is busy or reconnecting. Please try again shortly.");
                PublishActivity();
            }
        }
    }

    private void ActivityPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        string? method = e.Mode == PowerModes.Suspend ? "prepareForSleep" : e.Mode == PowerModes.Resume ? "resumeAfterSleep" : null;
        if (method is not null && !Exiting) Dispatcher.BeginInvoke(() => _ = ActivityCommandAsync(method));
    }
}
