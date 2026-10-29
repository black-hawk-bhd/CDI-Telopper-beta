using System.Windows;
using System.Windows.Threading;

namespace EEWTelop.Wpf.Services;

public interface IConfirmationService
{
    bool ConfirmProductionTest();

    bool ConfirmDisconnect();

    bool ConfirmProfileApply(string differences) => true;

    bool ConfirmDeleteAllTestCases(int count) => true;

    bool ConfirmResetAllSettings() => false;
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

    public bool ConfirmResetAllSettings() => MessageBox.Show(
        "すべての設定を初期状態に戻し、保存して反映しますか？\n\n" +
        "受信元・表示・フィルター・音声・出力・履歴・安全設定などを初期化します。" +
        "APIキー・AXISトークン・OBSパスワード・音声ファイルの指定・編集中の未保存設定も失われます。\n" +
        "受信接続やOBS出力が再設定され、OBSのURLが変わる場合があります。この操作は元に戻せません。\n\n" +
        "保存済み電文・ログ・テストデータ・保存済みプロファイルは削除しません。" +
        "DMDATAのOAuth認可は解除しません。解除する場合は受信タブで別途操作してください。",
        "すべての設定を初期化する確認",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
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
