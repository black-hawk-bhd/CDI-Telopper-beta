using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Operations;
using EEWTelop.Wpf.Bootstrap;
using Forms = System.Windows.Forms;

namespace EEWTelop.Wpf;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application owns process-lifetime tray resources and disposes them from OnExit.")]
public partial class App : System.Windows.Application
{
    private AppServices? _services;
    private ControlWindow? _controlWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _trayImage;
    private readonly object _cleanupGate = new();
    private Task? _cleanupTask;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private int _fatalUiExceptionHandlingStarted;
    private bool _explicitExit;
    private bool _trayNoticeShown;

    public void ShowOperationalNotification(OperationalAlert alert)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ShowOperationalNotification(alert));
            return;
        }
        Forms.ToolTipIcon icon = alert.Severity switch
        {
            OperationalAlertSeverity.Error => Forms.ToolTipIcon.Error,
            OperationalAlertSeverity.Warning => Forms.ToolTipIcon.Warning,
            _ => Forms.ToolTipIcon.Info,
        };
        _trayIcon?.ShowBalloonTip(5000, alert.Title, alert.Message, icon);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _singleInstanceCoordinator = new SingleInstanceCoordinator();
            if (!_singleInstanceCoordinator.IsPrimaryInstance)
            {
                _singleInstanceCoordinator.NotifyPrimaryInstance();
                Shutdown(0);
                return;
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _services = AppComposition.CreateDefault();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            _controlWindow = new ControlWindow(_services);
            MainWindow = _controlWindow;
            InitializeTrayIcon();
            _controlWindow.Closing += OnControlWindowClosing;
            _controlWindow.Show();
            _singleInstanceCoordinator.StartListening(() =>
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    _ = Dispatcher.BeginInvoke((Action)RestoreControlWindow);
                }
            });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            MessageBox.Show(
                $"CDI-Telopperを起動できませんでした。\n{exception.Message}",
                "CDI-Telopper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Task cleanup = GetOrStartCleanup(
            "ApplicationExit",
            "アプリケーション終了に伴う後処理を開始しました。");
        WaitWithDispatcher(cleanup);
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        if (_controlWindow is not null)
        {
            _controlWindow.Closing -= OnControlWindowClosing;
        }

        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _trayImage?.Dispose();
        _trayIcon = null;
        _trayMenu = null;
        _trayImage = null;
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceCoordinator = null;
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Task cleanup = GetOrStartCleanup(
            "SessionEnding",
            "Windowsの終了またはサインアウトに伴う後処理を開始しました。");
        WaitWithDispatcher(cleanup);
        base.OnSessionEnding(e);
    }

    private void InitializeTrayIcon()
    {
        string? processPath = Environment.ProcessPath;
        _trayImage = !string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath)
            ? Icon.ExtractAssociatedIcon(processPath)
            : null;
        _trayImage ??= (Icon)SystemIcons.Application.Clone();

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("CDI-Telopperを開く", null, (_, _) => RestoreControlWindow());
        _trayMenu.Items.Add(
            "受信・過去電文を確認",
            null,
            (_, _) => OpenTelegramReviewWindow());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("終了", null, (_, _) => _ = ExitApplicationAsync());
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayImage,
            Text = "CDI-Telopper",
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreControlWindow();
    }

    private void OnControlWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_explicitExit)
        {
            return;
        }

        e.Cancel = true;
        _controlWindow?.Hide();
        WriteInformation("MinimizedToTray", "操作画面を閉じずタスクトレイへ格納しました。受信と出力は継続します。");
        if (!_trayNoticeShown && _trayIcon is not null)
        {
            _trayNoticeShown = true;
            _trayIcon.ShowBalloonTip(
                2500,
                "CDI-Telopper",
                "バックグラウンドで受信・出力を継続しています。",
                Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreControlWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)RestoreControlWindow);
            return;
        }

        if (_controlWindow is null)
        {
            return;
        }

        _controlWindow.Show();
        if (_controlWindow.WindowState == WindowState.Minimized)
        {
            _controlWindow.WindowState = WindowState.Normal;
        }

        _controlWindow.Activate();
        _controlWindow.Topmost = true;
        _controlWindow.Topmost = false;
        _controlWindow.Focus();
        WriteInformation("RestoredFromTray", "タスクトレイから操作画面を復元しました。");
    }

    private void OpenTelegramReviewWindow()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke((Action)OpenTelegramReviewWindow);
            return;
        }

        _controlWindow?.ShowTelegramReviewWindow();
        WriteInformation(
            "TelegramReviewOpenedFromTray",
            "タスクトレイから受信・過去電文確認画面を開きました。");
    }

    private async Task ExitApplicationAsync()
    {
        await ShutdownApplicationAsync(
            0,
            "ExitRequested",
            "タスクトレイから終了が選択されました。").ConfigureAwait(false);
    }

    private async Task ShutdownApplicationAsync(
        int exitCode,
        string eventName,
        string message,
        Exception? exception = null)
    {
        await GetOrStartCleanup(eventName, message, exception).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_controlWindow is not null)
            {
                _controlWindow.Closing -= OnControlWindowClosing;
                _controlWindow.Close();
            }

            Shutdown(exitCode);
        })
            .Task.ConfigureAwait(false);
    }

    private Task GetOrStartCleanup(
        string eventName,
        string message,
        Exception? exception = null)
    {
        lock (_cleanupGate)
        {
            _cleanupTask ??= CleanupCoreAsync(eventName, message, exception);
            return _cleanupTask;
        }
    }

    private async Task CleanupCoreAsync(
        string eventName,
        string message,
        Exception? exception)
    {
        _explicitExit = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        if (_services is not null)
        {
            try
            {
                await _services.LogWriter.WriteAsync(new AppLogEntry(
                    _services.Clock.UtcNow,
                    exception is null ? AppLogLevel.Information : AppLogLevel.Critical,
                    eventName,
                    message,
                    exception)).ConfigureAwait(false);
            }
            catch (Exception loggingException) when (loggingException is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine(loggingException);
            }
        }

        if (_controlWindow is not null)
        {
            try
            {
                Task disposeTask = await Dispatcher.InvokeAsync(
                    () => _controlWindow.DisposeAsync().AsTask())
                    .Task.ConfigureAwait(false);
                await disposeTask.ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (cleanupException is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine(cleanupException);
            }
        }
        else if (_services is not null)
        {
            try
            {
                await _services.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (cleanupException is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine(cleanupException);
            }
        }
    }

    private void WaitWithDispatcher(Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => Dispatcher.BeginInvoke((Action)(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private void WriteInformation(string eventName, string message)
    {
        if (_services is not null)
        {
            _ = _services.LogWriter.WriteAsync(new AppLogEntry(
                _services.Clock.UtcNow,
                AppLogLevel.Information,
                eventName,
                message)).AsTask();
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        // A MessageBox runs a nested dispatcher loop. If the UI keeps throwing while
        // that loop is active, this handler can otherwise re-enter and create an
        // unbounded stack of dialogs. Preserve only the first failure and shut down once.
        if (Interlocked.Exchange(ref _fatalUiExceptionHandlingStarted, 1) != 0)
        {
            return;
        }

        // Persist the originating exception before showing UI. The process may fail
        // again while the modal dialog is open, so logging after it is not reliable.
        WriteCritical("UiUnhandledException", e.Exception);

        try
        {
            MessageBox.Show(
                "表示の正確性を保証できないエラーが発生しました。アプリを再起動してください。",
                "CDI-Telopper システム異常・要確認",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception dialogException) when (dialogException is not StackOverflowException)
        {
            WriteCritical("UiFatalDialogFailed", dialogException);
        }

        _ = ShutdownApplicationAsync(
            -1,
            "UiUnhandledExceptionShutdown",
            "未処理例外を記録したため終了します。");
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        _ = Dispatcher.BeginInvoke(() =>
        {
            _ = ShutdownApplicationAsync(
                -1,
                "TaskUnhandledException",
                "未監視タスク例外のため終了します。",
                e.Exception);
        });
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            if (e.IsTerminating)
            {
                try
                {
                    Task cleanup = Dispatcher.CheckAccess()
                        ? GetOrStartCleanup(
                            "DomainUnhandledException",
                            "未処理例外のため終了します。",
                            exception)
                        : Dispatcher.Invoke(() => GetOrStartCleanup(
                            "DomainUnhandledException",
                            "未処理例外のため終了します。",
                            exception));
                    if (Dispatcher.CheckAccess())
                    {
                        WaitWithDispatcher(cleanup);
                    }
                    else
                    {
                        cleanup.GetAwaiter().GetResult();
                    }
                }
                catch (Exception cleanupException) when (cleanupException is not StackOverflowException)
                {
                    WriteCritical("DomainUnhandledException", cleanupException);
                }
            }
            else
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _ = ShutdownApplicationAsync(
                        -1,
                        "DomainUnhandledException",
                        "未処理例外のため終了します。",
                        exception);
                });
            }
        }
    }

    private void WriteCritical(string eventName, Exception exception)
    {
        try
        {
            _services?.LogWriter.WriteAsync(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppLogLevel.Critical,
                eventName,
                "未処理例外のため表示を継続せず終了します。",
                exception)).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception loggingException) when (loggingException is not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine(loggingException);
        }
    }
}
