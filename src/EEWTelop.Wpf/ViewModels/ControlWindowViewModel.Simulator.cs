using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Testing;

namespace EEWTelop.Wpf.ViewModels;

public sealed partial class ControlWindowViewModel
{
    private CancellationTokenSource? _simulatorStop;
    private Task _simulatorTask = Task.CompletedTask;
    private bool _simulatorConnected;
    private int _simulatorPresentationGeneration;
    public bool IsSimulatorCleanRehearsal { get; private set; }
    public bool CanToggleSimulatorRehearsal => _simulatorConnected && _simulatorStop is { IsCancellationRequested: false };

    public void ToggleSimulatorRehearsal()
    {
        if (!CanToggleSimulatorRehearsal) return;
        IsSimulatorCleanRehearsal = !IsSimulatorCleanRehearsal;
        OnPropertyChanged(nameof(IsSimulatorCleanRehearsal));
        _simulatorPresentationGeneration++;
        ClearDisplay();
        SimulatorStatus = "表示方式を切り替えました。Simulatorから電文を再送信してください。";
    }

    private void ResetSimulatorRehearsal()
    {
        _simulatorConnected = false;
        IsSimulatorCleanRehearsal = false;
        OnPropertyChanged(nameof(IsSimulatorCleanRehearsal));
    }
    private string _simulatorStatus = "未接続（訓練専用）";
    public string SimulatorStatus
    {
        get => _simulatorStatus;
        private set { _simulatorStatus = value; OnPropertyChanged(); }
    }

    public Task ConnectSimulatorAsync(string address, string token)
    {
        if (_simulatorStop is not null) return _simulatorTask;
        if (!CanUseTests || IsConnectedOrConnecting || IsReceptionRunning)
        {
            SimulatorStatus = "本番との混在防止のため、接続欄でAPI受信を切断してから開始してください。";
            return Task.CompletedTask;
        }
        CancelTestScenario();
        ResetSimulatorRehearsal();
        ClearDisplay();
        var stop = new CancellationTokenSource();
        _simulatorStop = stop;
        SimulatorStatus = "シミュレーターへ接続中（訓練専用）";
        _simulatorTask = ReceiveSimulatorAsync(address, token, stop);
        return _simulatorTask;
    }

    public void DisconnectSimulator()
    {
        ResetSimulatorRehearsal();
        _simulatorStop?.Cancel();
    }

    private async Task ReceiveSimulatorAsync(string address, string token, CancellationTokenSource stop)
    {
        var coordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
        var eewComposer = new ConcurrentEewProgramComposer();
        TsunamiEvent? latestForecast = null;
        int presentationGeneration = _simulatorPresentationGeneration;
        string? apiSession = _obsServer?.BeginApiRehearsal("simulator");
        try
        {
            await DisasterSimulatorClient.RunAsync(address, token, async (events, reset) =>
            {
                if (reset || events.Any(item => item.IsExpired || item.IsCancelled))
                    await StopAudioAsync().ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (stop.IsCancellationRequested) return;
                    if (reset || presentationGeneration != _simulatorPresentationGeneration)
                    {
                        if (reset) ClearDisplay();
                        presentationGeneration = _simulatorPresentationGeneration;
                        coordinator = new PriorityCoordinator(_services.Clock, _settings.Display);
                        eewComposer = new ConcurrentEewProgramComposer();
                    }
                    ApplySimulatorUpdates(events, latestForecast, coordinator, eewComposer);
                    SimulatorStatus = $"訓練受信中・更新 {events.Count} 件（本番受信は停止中）";
                }, stop.Token).ConfigureAwait(false);
            }, () => _dispatcher.Invoke(() =>
            {
                _simulatorConnected = true;
                SimulatorStatus = "接続済み・新着訓練待ち（初期状態は再表示しません）";
            }), stop.Token, (snapshot, forecast, reset) =>
            {
                // Read the forecast before displaying any update in this frame, even when the quake is listed first.
                latestForecast = forecast;
                if (reset) apiSession = _obsServer?.BeginApiRehearsal("simulator");
                if (apiSession is null) return;
                _obsServer?.SetApiRehearsalHealth(apiSession, "healthy");
                _obsServer?.ReplaceApiRehearsalSnapshot(apiSession, snapshot);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!stop.IsCancellationRequested)
                await _dispatcher.InvokeAsync(() => SimulatorStatus = "シミュレーターの応答が途絶えました。再接続してください。");
        }
        catch (Exception)
        {
            // URLs and authentication errors may contain secrets: do not display raw exceptions.
            await _dispatcher.InvokeAsync(() => SimulatorStatus = "接続終了：SimulatorのAPI起動、ローカルURL・トークン・形式を確認してください。");
        }
        finally
        {
            if (stop.IsCancellationRequested && apiSession is not null) _obsServer?.EndApiRehearsal(apiSession);
            else _obsServer?.SetApiRehearsalHealth(apiSession, "failed");
            await StopAudioAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                ResetSimulatorRehearsal();
                ClearDisplay();
                if (stop.IsCancellationRequested) SimulatorStatus = "切断済み・訓練表示を消去しました。本番受信は手動で再開してください。";
                _simulatorStop = null;
            });
            stop.Dispose();
        }
    }

    internal void ApplySimulatorUpdates(IReadOnlyList<DisasterEvent> events, TsunamiEvent? forecast,
        PriorityCoordinator coordinator, ConcurrentEewProgramComposer eewComposer)
    {
        DisplaySettings display = _settings.Display;
        TsunamiEvent? visibleForecast = _settings.Filter.Tsunami ? forecast : null;
        if (display.SuppressScalePromptWhenTsunamiWarningActive && visibleForecast is { } activeForecast)
        {
            bool Matches(DisplayProgram program) => program.SourceMode == SourceMode.ManualTest &&
                program.QuakeIssueType == QuakeIssueType.ScalePrompt &&
                IsSimulatorWarningFor(activeForecast, program.EventId, program.ScalePromptEarthquakeId, program.IssuedAt);

            _obsSnapshotStore.ClearQuakeProgram(_services.Clock.UtcNow, Matches);
            if (coordinator.RemoveQuakePrograms(Matches) && ReferenceEquals(Volatile.Read(ref _activeCoordinator), coordinator))
                ApplyDisplaySnapshot(coordinator.Evaluate());
        }

        foreach (DisasterEvent item in events)
        {
            // Terminal updates remove the training display; an all-clear gets its own tsunami caption.
            if (item.IsExpired || (item.IsCancelled && item is not TsunamiEvent { Issue.InformationType: not "取消" }))
            {
                ClearDisplay(item.Kind);
                coordinator.Clear(item.Kind);
                if (item.Kind == EventKind.Eew) eewComposer.Clear();
                continue;
            }

            bool linkedWarning = visibleForecast is { } currentForecast &&
                item is QuakeEvent { IssueType: QuakeIssueType.ScalePrompt } quake &&
                IsSimulatorWarningFor(currentForecast, quake.Id, quake.JmaEarthquakeId, quake.IssuedAt);
            if (linkedWarning && display.SuppressScalePromptWhenTsunamiWarningActive)
            {
                DisplayProgram review = _services.PageComposer.Compose(item, display);
                _obsSnapshotStore.AddMonitorTelegram(item, review, display, EventIngestionPipeline.ScalePromptSuppressionReason);
                continue;
            }

            var scenario = new TestScenario("disaster-simulator", "Disaster Simulator（訓練）", item);
            DisplayTestScenarioStep(scenario, new TestScenarioStep(TimeSpan.Zero, item), 1, eewComposer, coordinator,
                omitScalePromptTsunamiCaution: linkedWarning && display.OmitScalePromptCautionWhenTsunamiWarningActive);
        }
    }

    private static bool IsSimulatorWarningFor(TsunamiEvent forecast, EventId quakeId, EventId? jmaId, DateTimeOffset quakeIssuedAt)
    {
        // Archived replays retain the original validity window and report times.
        DateTimeOffset referenceTime = quakeIssuedAt > forecast.IssuedAt ? quakeIssuedAt : forecast.IssuedAt;
        return (forecast.Id == quakeId || (jmaId is { } id && forecast.JmaEarthquakeIds.Contains(id))) &&
            TsunamiEventStateAccumulator.HasActiveWarning(forecast, referenceTime);
    }
}
