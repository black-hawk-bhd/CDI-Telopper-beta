using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;

namespace EEWTelop.Wpf.ViewModels;

public sealed partial class ControlWindowViewModel
{
    private bool _isResettingAllSettings;
    private Task _settingsResetTask = Task.CompletedTask;

    public bool IsResettingAllSettings => _isResettingAllSettings;

    public Task ResetAllSettingsAsync()
    {
        if (IsDmdataOAuthBusy || IsResettingAllSettings || _disposed) return Task.CompletedTask;
        _settingsResetTask = ResetAllSettingsCoreAsync();
        return _settingsResetTask;
    }

    private async Task ResetAllSettingsCoreAsync()
    {
        if (IsDmdataOAuthBusy || IsResettingAllSettings || _disposed) return;
        bool confirmed = false;
        _dispatcher.Invoke(() => confirmed = _confirmationService.ConfirmResetAllSettings());
        // A confirmation dialog runs a nested UI loop, so check again afterwards.
        if (!confirmed || IsDmdataOAuthBusy || IsResettingAllSettings || _disposed) return;

        SetSettingsResetBusy(true);
        bool refreshGateHeld = false;
        try
        {
            // Finish any in-flight token renewal before clearing its saved credentials.
            await _axisTokenRefreshGate.WaitAsync().ConfigureAwait(false);
            refreshGateHeld = true;
            AppSettings defaults = AppSettings.CreateDefault();
            _dispatcher.Invoke(() =>
            {
                ReplaceSettingsEditor(defaults);
                MinimumLogLevel = AppLogLevel.Debug;
                ReceptionStatusText = "すべての設定を初期化しています…";
            });
            // Use the complete default document, including fields absent from the editor.
            await ApplySettingsAsync(defaults).ConfigureAwait(false);
            await WriteLogAsync(AppLogLevel.Information, "AllSettingsReset",
                "すべての設定を初期状態に戻し、保存して反映しました。").ConfigureAwait(false);
            _dispatcher.Invoke(() => ReceptionStatusText = "すべての設定を初期状態に戻し、保存して反映しました");
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            await WriteLogAsync(AppLogLevel.Error, "AllSettingsResetFailed",
                "設定の初期化・保存・接続の再設定を完了できませんでした。", exception).ConfigureAwait(false);
            _dispatcher.Invoke(() => ReceptionStatusText = "設定の初期化を完了できませんでした（ログを確認）");
        }
        finally
        {
            if (refreshGateHeld) _axisTokenRefreshGate.Release();
            _dispatcher.Invoke(() => SetSettingsResetBusy(false));
        }
    }

    private void ReplaceSettingsEditor(AppSettings settings)
    {
        SettingsEditorViewModel oldEditor = Settings;
        oldEditor.PropertyChanged -= OnDmdataContractSettingsChanged;
        Settings = new SettingsEditorViewModel(settings);
        Settings.PropertyChanged += OnDmdataContractSettingsChanged;
        DmdataContracts.Invalidate();
        OnPropertyChanged(nameof(Settings));
        SettingsEditorChanged?.Invoke(oldEditor, Settings);
    }

    private void SetSettingsResetBusy(bool value)
    {
        _isResettingAllSettings = value;
        OnPropertyChanged(nameof(IsResettingAllSettings));
        OnPropertyChanged(nameof(CanManageDmdataOAuth));
        ResetAllSettingsCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        ApplyProfileCommand.RaiseCanExecuteChanged();
    }
}
