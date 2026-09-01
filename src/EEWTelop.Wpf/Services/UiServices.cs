using System.Windows;
using System.Windows.Threading;

namespace EEWTelop.Wpf.Services;

public interface IConfirmationService
{
    bool ConfirmProductionTest();

    bool ConfirmDisconnect();

    bool ConfirmProfileApply(string differences) => true;

    bool ConfirmDeleteAllTestCases(int count) => true;
}

public interface IUiDispatcher
{
    void Invoke(Action action);

    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class MessageBoxConfirmationService : IConfirmationService
{
    public bool ConfirmProductionTest() => MessageBox.Show(
        "本番受信中です。訓練テロップをプレビューへ表示しますか？\n本番出力と混同しないことを確認してください。",
        "本番中のテスト確認",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmDisconnect() => MessageBox.Show(
        "APIとの接続を切断しますか？",
        "切断の確認",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmProfileApply(string differences) => MessageBox.Show(
        $"次の設定差分を適用しますか？\n\n{differences}",
        "プロファイルの適用確認",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmDeleteAllTestCases(int count) => MessageBox.Show(
        $"登録済みのテストケース {count}件をすべて削除しますか？\nこの操作は元に戻せません。",
        "テストケースの一括削除",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    public async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        DispatcherOperation operation = dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
        await operation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invoke(action);
        return Task.CompletedTask;
    }
}
