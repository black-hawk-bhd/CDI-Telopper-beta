using System.Windows;
using System.ComponentModel;
using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Operations;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Bootstrap;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Services;
using EEWTelop.Wpf.Testing;
using EEWTelop.Wpf.ViewModels;
using Microsoft.Win32;

namespace EEWTelop.Wpf;

public partial class ControlWindow : Window, IAsyncDisposable
{
    private readonly ControlWindowViewModel _viewModel;
    private readonly E2ETestPipeServer? _e2eTestPipeServer;
    private PreviewWindow? _previewWindow;
    private TelegramReviewWindow? _telegramReviewWindow;
    private bool _disposed;

    public ControlWindow()
        : this(AppComposition.CreateDefault())
    {
    }

    public ControlWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        InitializeComponent();
        var obsSnapshots = new ObsSnapshotStore(services.InitialSettings.Display, services.Clock.UtcNow);
        var obsServer = new ObsLocalViewServer(
            obsSnapshots,
            services.Clock,
            services.LogWriter,
            services.InitialSettings.Obs.SnapshotIntervalMilliseconds);
        var obsBrowserSourceSynchronizer = new ObsBrowserSourceSynchronizer(services.LogWriter);
        _viewModel = new ControlWindowViewModel(
            services,
            services.InitialSettings,
            new MessageBoxConfirmationService(),
            new WpfUiDispatcher(Dispatcher),
            obsSnapshots,
            obsServer,
            obsBrowserSourceSynchronizer);
        DataContext = _viewModel;
        ObsWebSocketPasswordBox.Password = _viewModel.Settings.ObsWebSocketPassword;
        DmdataCredentialBox.Password = _viewModel.Settings.DmdataCredential;
        AxisAccessTokenBox.Password = _viewModel.Settings.AxisAccessToken;
        _viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
        _viewModel.ShowPreviewRequested += OnShowPreviewRequested;
        _viewModel.ShowTelegramReviewRequested += OnShowTelegramReviewRequested;
        _viewModel.EditSubtitleRequested += OnEditSubtitleRequested;
        _viewModel.EditPendingSubtitleRequested += OnEditPendingSubtitleRequested;
        _viewModel.EditPreDisplaySubtitleRequested += OnEditPreDisplaySubtitleRequested;
        _viewModel.EditSubtitlePhraseTemplatesRequested += OnEditSubtitlePhraseTemplatesRequested;
        _viewModel.CopyTextRequested += OnCopyTextRequested;
        _viewModel.ExportDiagnosticsRequested += OnExportDiagnosticsRequested;
        _viewModel.BrowseAudioFileRequested += OnBrowseAudioFileRequested;
        _viewModel.BrowseHistoryXmlFileRequested += OnBrowseHistoryXmlFileRequested;
        _viewModel.ImportProfileRequested += OnImportProfileRequested;
        _viewModel.ExportProfileRequested += OnExportProfileRequested;
        _viewModel.ImportTestFilesRequested += OnImportTestFilesRequested;
        _viewModel.ImportDmdataArchiveRequested += OnImportDmdataArchiveRequested;
        _viewModel.ImportTestPackageRequested += OnImportTestPackageRequested;
        _viewModel.ExportTestPackageRequested += OnExportTestPackageRequested;
        _viewModel.OperatorNotificationRequested += OnOperatorNotificationRequested;
        _viewModel.SettingsEditorChanged += OnSettingsEditorChanged;
        _e2eTestPipeServer = E2ETestPipeServer.StartIfEnabled(
            async (json, cancellationToken) =>
            {
                var raw = new RawProviderMessage(
                    "p2pquake",
                    json,
                    SourceMode.Sandbox,
                    services.Clock.UtcNow);
                await services.ReceptionService.ProcessAsync(raw, cancellationToken)
                    .ConfigureAwait(false);
            });
    }

    protected override async void OnClosed(EventArgs e)
    {
        await DisposeAsync();
        base.OnClosed(e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.ShowPreviewRequested -= OnShowPreviewRequested;
        _viewModel.ShowTelegramReviewRequested -= OnShowTelegramReviewRequested;
        _viewModel.EditSubtitleRequested -= OnEditSubtitleRequested;
        _viewModel.EditPendingSubtitleRequested -= OnEditPendingSubtitleRequested;
        _viewModel.EditPreDisplaySubtitleRequested -= OnEditPreDisplaySubtitleRequested;
        _viewModel.EditSubtitlePhraseTemplatesRequested -= OnEditSubtitlePhraseTemplatesRequested;
        _viewModel.CopyTextRequested -= OnCopyTextRequested;
        _viewModel.ExportDiagnosticsRequested -= OnExportDiagnosticsRequested;
        _viewModel.BrowseAudioFileRequested -= OnBrowseAudioFileRequested;
        _viewModel.BrowseHistoryXmlFileRequested -= OnBrowseHistoryXmlFileRequested;
        _viewModel.ImportProfileRequested -= OnImportProfileRequested;
        _viewModel.ExportProfileRequested -= OnExportProfileRequested;
        _viewModel.ImportTestFilesRequested -= OnImportTestFilesRequested;
        _viewModel.ImportDmdataArchiveRequested -= OnImportDmdataArchiveRequested;
        _viewModel.ImportTestPackageRequested -= OnImportTestPackageRequested;
        _viewModel.ExportTestPackageRequested -= OnExportTestPackageRequested;
        _viewModel.OperatorNotificationRequested -= OnOperatorNotificationRequested;
        _viewModel.SettingsEditorChanged -= OnSettingsEditorChanged;
        _viewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _previewWindow?.Close();
        _telegramReviewWindow?.Close();
        if (_e2eTestPipeServer is not null)
        {
            await _e2eTestPipeServer.DisposeAsync().ConfigureAwait(false);
        }
        await _viewModel.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnShowPreviewRequested(object? sender, EventArgs e)
    {
        if (_previewWindow is null)
        {
            _previewWindow = new PreviewWindow(_viewModel.Overlay) { Owner = this };
            _previewWindow.Closed += (_, _) => _previewWindow = null;
        }

        _previewWindow.Show();
        _previewWindow.Activate();
    }

    private void OnShowTelegramReviewRequested()
    {
        ShowTelegramReviewWindow();
    }

    internal void ShowTelegramReviewWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)ShowTelegramReviewWindow);
            return;
        }

        if (_telegramReviewWindow is null)
        {
            // 操作画面とは独立させ、操作画面を最小化・トレイ格納しても表示を維持する。
            _telegramReviewWindow = new TelegramReviewWindow(_viewModel);
            _telegramReviewWindow.Closed += (_, _) => _telegramReviewWindow = null;
        }

        _telegramReviewWindow.Show();
        if (_telegramReviewWindow.WindowState == WindowState.Minimized)
        {
            _telegramReviewWindow.WindowState = WindowState.Normal;
        }

        _telegramReviewWindow.Activate();
        _telegramReviewWindow.Focus();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsEditorViewModel.ObsWebSocketPassword) &&
            ObsWebSocketPasswordBox.Password != _viewModel.Settings.ObsWebSocketPassword)
        {
            ObsWebSocketPasswordBox.Password = _viewModel.Settings.ObsWebSocketPassword;
        }
        else if (e.PropertyName == nameof(SettingsEditorViewModel.AxisAccessToken) &&
                 AxisAccessTokenBox.Password != _viewModel.Settings.AxisAccessToken)
        {
            AxisAccessTokenBox.Password = _viewModel.Settings.AxisAccessToken;
        }
        else if (e.PropertyName == nameof(SettingsEditorViewModel.DmdataCredential) &&
                 DmdataCredentialBox.Password != _viewModel.Settings.DmdataCredential)
        {
            DmdataCredentialBox.Password = _viewModel.Settings.DmdataCredential;
        }
    }

    private void OnSettingsEditorChanged(SettingsEditorViewModel oldEditor, SettingsEditorViewModel newEditor)
    {
        oldEditor.PropertyChanged -= OnSettingsPropertyChanged;
        newEditor.PropertyChanged += OnSettingsPropertyChanged;
        ObsWebSocketPasswordBox.Password = newEditor.ObsWebSocketPassword;
        DmdataCredentialBox.Password = newEditor.DmdataCredential;
        AxisAccessTokenBox.Password = newEditor.AxisAccessToken;
    }

    private async void OnImportProfileRequested()
    {
        var dialog = new OpenFileDialog { Title = "設定プロファイルを取り込む", Filter = "CDI-Telopper profile (*.qtprofile.json)|*.qtprofile.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) await RunDialogOperationAsync(() => _viewModel.ImportProfileAsync(dialog.FileName));
    }

    private async void OnExportProfileRequested(string name)
    {
        var dialog = new SaveFileDialog { Title = "設定プロファイルを書き出す", Filter = "CDI-Telopper profile (*.qtprofile.json)|*.qtprofile.json", FileName = name + ".qtprofile.json" };
        if (dialog.ShowDialog(this) == true) await RunDialogOperationAsync(() => _viewModel.ExportProfileAsync(name, dialog.FileName));
    }

    private async void OnImportTestFilesRequested()
    {
        var dialog = new OpenFileDialog { Title = "生データをテストライブラリーへ登録", Filter = "対応ファイル|*.xml;*.json;*.png", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await RunDialogOperationAsync(() => _viewModel.ImportTestFilesAsync(dialog.FileNames));
    }

    private async void OnImportDmdataArchiveRequested()
    {
        var dialog = new OpenFileDialog
        {
            Title = "dmdata生データのtelegrams.jsonを選択",
            Filter = "dmdata index (telegrams.json)|telegrams.json|JSON (*.json)|*.json",
            FileName = "telegrams.json",
        };
        if (dialog.ShowDialog(this) == true)
            await RunDialogOperationAsync(() => _viewModel.ImportDmdataArchiveAsync(dialog.FileName));
    }

    private async void OnImportTestPackageRequested()
    {
        var dialog = new OpenFileDialog { Title = "テストケースZIPを取り込む", Filter = "ZIP archive (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) == true) await RunDialogOperationAsync(() => _viewModel.ImportTestPackageAsync(dialog.FileName));
    }

    private async void OnExportTestPackageRequested(OperationalTestCaseViewModel item)
    {
        var dialog = new SaveFileDialog { Title = "テストケースを書き出す", Filter = "ZIP archive (*.zip)|*.zip", FileName = item.Manifest.Name + ".zip" };
        if (dialog.ShowDialog(this) == true) await RunDialogOperationAsync(() => _viewModel.ExportTestPackageAsync(item.Manifest.Id, dialog.FileName));
    }

    private void OnOperatorNotificationRequested(OperationalAlert alert) =>
        (System.Windows.Application.Current as App)?.ShowOperationalNotification(alert);

    private async Task RunDialogOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            MessageBox.Show(this, exception.Message, "CDI-Telopper", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnEditSubtitleRequested(
        DisplayProgram sourceProgram,
        DisplayProgram editableProgram)
    {
        var dialog = new SubtitleEditorWindow(sourceProgram, editableProgram) { Owner = this };
        if (dialog.ShowDialog() == true &&
            !_viewModel.TryApplySubtitleEdit(sourceProgram, dialog.EditedProgram))
        {
            MessageBox.Show(
                this,
                "編集中に新しい情報へ切り替わったため、変更を反映しませんでした。現在の字幕を確認して、もう一度編集してください。",
                "字幕編集",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnEditPendingSubtitleRequested(IReadOnlyList<DisplayProgram> programs)
    {
        var selectionDialog = new PendingSubtitleSelectionWindow(programs) { Owner = this };
        if (selectionDialog.ShowDialog() != true || selectionDialog.SelectedProgram is not { } sourceProgram)
        {
            return;
        }

        var editor = new SubtitleEditorWindow(
            sourceProgram,
            _viewModel.GetEditedProgram(sourceProgram))
        {
            Owner = this,
        };
        if (editor.ShowDialog() == true &&
            !_viewModel.TryApplySubtitleEdit(sourceProgram, editor.EditedProgram))
        {
            MessageBox.Show(
                this,
                "編集中に待機情報が更新または取り消されたため、変更を反映しませんでした。",
                "表示前の字幕を編集",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnEditPreDisplaySubtitleRequested(IReadOnlyList<DisplayProgram> programs)
    {
        var selectionDialog = new PendingSubtitleSelectionWindow(programs)
        {
            Owner = this,
            Title = "作画前の字幕を選択",
        };
        if (selectionDialog.ShowDialog() != true ||
            selectionDialog.SelectedProgram is not { } sourceProgram)
        {
            return;
        }

        var editor = new SubtitleEditorWindow(
            sourceProgram,
            sourceProgram,
            releaseAfterEditing: true)
        {
            Owner = this,
        };
        if (editor.ShowDialog() == true &&
            !_viewModel.TryReleasePreDisplaySubtitle(sourceProgram, editor.EditedProgram))
        {
            MessageBox.Show(
                this,
                "編集中に同じ情報の更新報を受信したため、古い字幕は送出しませんでした。最新の作画前字幕を確認してください。",
                "作画前の字幕を編集",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnEditSubtitlePhraseTemplatesRequested()
    {
        var dialog = new SubtitlePhraseTemplateWindow(
            _viewModel.Settings.SubtitlePhraseOverrides)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.Settings.SetSubtitlePhraseOverrides(dialog.PhraseOverrides);
        }
    }

    private static void OnCopyTextRequested(object? sender, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private async void OnExportDiagnosticsRequested(object? sender, EventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "診断情報を保存",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"CDI-Telopper-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                await _viewModel.ExportDiagnosticsAsync(dialog.FileName);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                MessageBox.Show(
                    this,
                    $"診断ZIPを保存できませんでした。\n{exception.Message}",
                    "CDI-Telopper",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void OnBrowseAudioFileRequested(AudioCueId cue)
    {
        var dialog = new OpenFileDialog
        {
            Title = "音声ファイルを選択",
            Filter = "音声ファイル|*.wav;*.mp3;*.ogg|すべてのファイル|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetAudioFilePath(cue, dialog.FileName);
        }
    }

    private void OnBrowseHistoryXmlFileRequested()
    {
        var dialog = new OpenFileDialog
        {
            Title = "テストする気象庁防災情報XMLを選択",
            Filter = "XMLファイル (*.xml)|*.xml",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.Settings.LocalHistoryXmlFilePath = dialog.FileName;
            _viewModel.Settings.HistoryApi = HistoryApi.LocalJmaXml;
        }
    }

    private void OnObsWebSocketPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox &&
            DataContext is ControlWindowViewModel viewModel)
        {
            viewModel.Settings.ObsWebSocketPassword = passwordBox.Password;
        }
    }

    private void OnAxisAccessTokenChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox &&
            DataContext is ControlWindowViewModel viewModel)
        {
            viewModel.Settings.AxisAccessToken = passwordBox.Password;
        }
    }

    private void OnDmdataCredentialChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox &&
            DataContext is ControlWindowViewModel viewModel)
        {
            viewModel.Settings.DmdataCredential = passwordBox.Password;
        }
    }

    private void OnSelectWeatherPrefecturesClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new WeatherPrefectureSelectionWindow(
            _viewModel.Settings.WeatherPrefectureCodes)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.Settings.SetWeatherPrefectureCodes(dialog.SelectedCodes);
        }
    }
}
