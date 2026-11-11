using System.IO;
using System.Security.Cryptography;
using EEWTelop.Application.Logging;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed partial class ControlWindowViewModel
{
    private string? _externalApiConnectionKeyError;

    public RelayCommand ToggleExternalApiConnectionKeySavingCommand { get; }

    public RelayCommand ReissueExternalApiConnectionKeyCommand { get; }

    public bool CanSaveExternalApiConnectionKey => _obsServer?.CanSaveExternalApiConnectionKey == true;

    public bool CanReissueExternalApiConnectionKey =>
        ExternalApiEnabled || _obsServer?.IsExternalApiConnectionKeySaved == true;

    public string ExternalApiConnectionKeySavingAction => _obsServer?.IsExternalApiConnectionKeySaved == true
        ? "接続キーの保存を解除" : "接続キーを保存";

    public string ExternalApiConnectionKeyStatus => _externalApiConnectionKeyError ??
        (_obsServer?.ExternalApiConnectionKeyLoadFailed == true
            ? "保存済みキーを読み込めません。接続キーを保存し直してください。"
            : _obsServer?.IsExternalApiConnectionKeySaved == true
                ? "接続キーをこのWindowsユーザー用に暗号化保存済み"
                : "接続キーは今回の有効化中のみ使用");

    private void ChangeExternalApiConnectionKey(bool reissue)
    {
        if (_obsServer is null || IsResettingAllSettings || _disposed) return;
        try
        {
            if (reissue)
            {
                _obsServer.ReissueExternalApiConnectionKey();
            }
            else
            {
                _obsServer.SetExternalApiConnectionKeySaved(!_obsServer.IsExternalApiConnectionKeySaved);
            }

            _externalApiConnectionKeyError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _externalApiConnectionKeyError = "接続キーの設定を変更できませんでした（ログを確認）";
            FireAndForgetLog(AppLogLevel.Error, "ExternalApiConnectionKeyChangeFailed",
                _externalApiConnectionKeyError);
        }

        RefreshExternalApiConnectionKeyControls();
    }

    private void RefreshExternalApiConnectionKeyControls()
    {
        OnPropertyChanged(nameof(ExternalApiConnectionKeySavingAction));
        OnPropertyChanged(nameof(ExternalApiConnectionKeyStatus));
        OnPropertyChanged(nameof(CanReissueExternalApiConnectionKey));
        OnPropertyChanged(nameof(ExternalApiUrlText));
    }
}
