using EEWTelop.Application.Configuration;
#if QTELOPPER_DMDATA_PROVIDER
using EEWTelop.Infrastructure.Dmdata.Configuration;
#endif

namespace EEWTelop.Wpf.ViewModels;

public sealed partial class ControlWindowViewModel
{
    private readonly CancellationTokenSource _dmdataOAuthStop = new();
    private CancellationTokenSource? _dmdataOAuthOperation;
    private Task _dmdataOAuthTask = Task.CompletedTask;
    private bool _isDmdataOAuthBusy;
    private bool _dmdataOAuthReceptionActive;
    private string _dmdataOAuthStatusText = "ブラウザー認証を利用できます。";

    public bool IsDmdataOAuthBusy => _isDmdataOAuthBusy;
    public bool CanManageDmdataOAuth => _services.DmdataOAuthService is not null &&
        !_isDmdataOAuthBusy && !_dmdataOAuthReceptionActive && !IsConnectedOrConnecting;
    public string DmdataOAuthStatusText => _dmdataOAuthStatusText;

    public Task AuthorizeDmdataOAuthAsync(Action<Uri> openBrowser)
    {
        if (!CanManageDmdataOAuth) return Task.CompletedTask;
        _dmdataOAuthTask = RunDmdataOAuthOperationAsync(openBrowser);
        return _dmdataOAuthTask;
    }

    public Task RevokeDmdataOAuthAsync()
    {
        if (!CanManageDmdataOAuth) return Task.CompletedTask;
        _dmdataOAuthTask = RunDmdataOAuthOperationAsync(null);
        return _dmdataOAuthTask;
    }

    public void CancelDmdataOAuth() => _dmdataOAuthOperation?.Cancel();

    private async Task RunDmdataOAuthOperationAsync(Action<Uri>? openBrowser)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_dmdataOAuthStop.Token);
        _dmdataOAuthOperation = stop;
        _isDmdataOAuthBusy = true;
        UpdateDmdataOAuthUi(openBrowser is null ? "認可を解除しています…" : "ブラウザーでDMDATA.JPへの認可を完了してください（5分以内）。");
        try
        {
            if (openBrowser is null)
            {
                await _services.DmdataOAuthService!.RevokeAsync(stop.Token).ConfigureAwait(false);
                UpdateDmdataOAuthUi("OAuth認可を解除しました。再接続にはブラウザー認証が必要です。");
            }
            else
            {
#if QTELOPPER_DMDATA_PROVIDER
                ProviderSettings provider = Settings.ToSettings(_settings).Provider with
                { DmdataAuthenticationMode = DmdataAuthenticationMode.OAuthAccessToken };
                DmdataProviderOptions options = DmdataProviderOptions.FromSettings(provider, BuildFeatures.ExtendedFeaturesEnabled);
                IReadOnlyList<string> errors = options.Validate();
                if (errors.Count > 0)
                {
                    UpdateDmdataOAuthUi(string.Join(" ", errors));
                    return;
                }
                await _services.DmdataOAuthService!.AuthorizeAsync(options.OAuthClientId,
                    options.OAuthScopes, openBrowser, stop.Token).ConfigureAwait(false);
                UpdateDmdataOAuthUi("OAuth認証済み。設定を保存してから受信を開始してください。");
#endif
            }
        }
        catch (OperationCanceledException)
        {
            UpdateDmdataOAuthUi("OAuth処理を中止したか、認証の待機時間が終了しました。");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            // OAuth service errors are sanitized; raw callback URLs and token bodies are never logged.
#if QTELOPPER_DMDATA_PROVIDER
            string message = exception is EEWTelop.Infrastructure.Dmdata.Security.DmdataOAuthException
                ? exception.Message : "OAuth処理を完了できませんでした。接続環境と暗号化保存先を確認してください。";
#else
            const string message = "この版ではDMDATA OAuthを利用できません。";
#endif
            UpdateDmdataOAuthUi(message);
        }
        finally
        {
            _dmdataOAuthOperation = null;
            _isDmdataOAuthBusy = false;
            UpdateDmdataOAuthUi();
        }
    }

    private void UpdateDmdataOAuthUi(string? message = null) => _dispatcher.Invoke(() =>
    {
        if (message is not null) _dmdataOAuthStatusText = message;
        OnPropertyChanged(nameof(DmdataOAuthStatusText));
        OnPropertyChanged(nameof(IsDmdataOAuthBusy));
        OnPropertyChanged(nameof(CanManageDmdataOAuth));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
    });
}
