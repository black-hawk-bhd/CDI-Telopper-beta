using System.Collections.ObjectModel;
using System.Globalization;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Wpf.Mvvm;
using EEWTelop.Wpf.Services;
#if QTELOPPER_DMDATA_PROVIDER
using EEWTelop.Infrastructure.Dmdata.Security;
using EEWTelop.Infrastructure.Dmdata.Transport;
#endif

namespace EEWTelop.Wpf.ViewModels;

public sealed class DmdataContractRowViewModel(DmdataContractInfo contract)
{
    public string PlanName => contract.PlanName;
    public string Classification => contract.Classification;
    public string Status => contract.ContractId is null ? "未契約" : contract.IsValid ? "契約中（有効）" : "契約あり（無効）";
    public string StatusColor => contract.ContractId is not null && contract.IsValid ? "#FF8FE5AA" : "#FFFFC66D";
    public string Price => string.Format(CultureInfo.CurrentCulture,
        "{0:N0}円／日・月最大 {1:N0}円", contract.DailyPriceYen, contract.MonthlyMaximumPriceYen);
    public string StartedAt => contract.StartedAt is { } start
        ? "契約開始：" + start.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture) : "契約開始：—";
    public string ConnectionCount => string.Format(CultureInfo.CurrentCulture,
        "この契約による接続可能数の増加：{0}", contract.AdditionalConnectionCount);
}

public sealed class DmdataContractsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDmdataContractService? _service;
    private readonly Func<ProviderSettings> _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _operation;
    private Task _task = Task.CompletedTask;
    private bool _isBusy;
    private bool _authorizationBusy;
    private bool _disposed;
    private int _version;
    private string _statusText = "未取得。「契約情報を更新」で取得します。";
    private string _retrievedAtText = string.Empty;

    public DmdataContractsViewModel(IDmdataContractService? service, Func<ProviderSettings> settings, IUiDispatcher dispatcher)
    {
        _service = service;
        _settings = settings;
        _dispatcher = dispatcher;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => CanRefresh);
        if (service is null) _statusText = "この版では契約情報の取得を利用できません。";
    }

    public ObservableCollection<DmdataContractRowViewModel> Items { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public bool IsBusy => _isBusy;
    public bool CanRefresh => !_disposed && _service is not null && !_isBusy && !_authorizationBusy;
    public string StatusText => _statusText;
    public string RetrievedAtText => _retrievedAtText;

    public Task RefreshAsync()
    {
        if (!CanRefresh) return Task.CompletedTask;
        _task = RefreshCoreAsync();
        return _task;
    }

    public void SetAuthorizationBusy(bool busy)
    {
        _authorizationBusy = busy;
        RefreshCommand.RaiseCanExecuteChanged();
    }

    public void Invalidate()
    {
        _version++;
        _operation?.Cancel();
        Items.Clear();
        _statusText = "認証設定が変更されました。契約情報を更新してください。";
        _retrievedAtText = string.Empty;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RetrievedAtText));
    }

    private async Task RefreshCoreAsync()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        stop.CancelAfter(TimeSpan.FromSeconds(25));
        _operation = stop;
        int version = _version;
        _isBusy = true;
        Items.Clear();
        _retrievedAtText = string.Empty;
        _statusText = "契約情報を取得しています…";
        Notify();
        try
        {
            ProviderSettings settings = _settings();
            DmdataContractSnapshot snapshot = await _service!.GetAsync(settings, stop.Token).ConfigureAwait(false);
            if (_disposed) return;
            _dispatcher.Invoke(() =>
            {
                if (_disposed || version != _version || stop.IsCancellationRequested) return;
                foreach (DmdataContractInfo item in snapshot.Items) Items.Add(new(item));
                _retrievedAtText = "最終取得：" + snapshot.RetrievedAtUtc.ToLocalTime()
                    .ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture);
                _statusText = snapshot.Items.Count == 0 ? "取得しました。APIから返された契約情報はありません。" : "契約情報を取得しました。";
            });
        }
        catch (OperationCanceledException)
        {
            SetFailure(version, "契約情報の取得を中止したか、待機時間が終了しました。契約状態は未確認です。");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            // Only our sanitized messages are shown. No raw API/transport errors or response bodies.
#if QTELOPPER_DMDATA_PROVIDER
            string message = exception switch
            {
                DmdataContractException => exception.Message,
                DmdataOAuthException { ErrorCode: "scope_required" } => "契約情報の閲覧権限（contract.list）がありません。受信を停止し、「契約情報も認可」で再認証してください。共通・独自クライアントの登録にもこの権限が必要です。",
                DmdataOAuthException => exception.Message,
                _ => "契約情報を取得できませんでした。接続環境と認証設定を確認してください。契約状態は未確認です。",
            };
#else
            const string message = "この版では契約情報を取得できません。";
#endif
            SetFailure(version, message);
        }
        finally
        {
            _operation = null;
            if (!_disposed) _dispatcher.Invoke(() =>
            {
                if (_disposed) return;
                _isBusy = false;
                Notify();
            });
        }
    }

    private void SetFailure(int version, string message)
    {
        if (_disposed) return;
        _dispatcher.Invoke(() =>
        {
            if (!_disposed && version == _version) _statusText = message;
        });
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RetrievedAtText));
        RefreshCommand.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        await _task.ConfigureAwait(false);
        _stop.Dispose();
    }
}
