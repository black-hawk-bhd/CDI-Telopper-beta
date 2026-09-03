# 設定の処理経路

設定変更は、画面だけでなく受信元、Normalizer、表示、OBS、保存形式へ影響します。この文書は設定項目を追加・変更するときの確認範囲を示します。

## 読み込み

```text
%LOCALAPPDATA%\QTelopper\2.x-beta\settings.json
        │
        ▼
JsonSettingsStore.LoadAsync
  ├─ JSON読込
  ├─ スキーマ移行
  ├─ 値と範囲の検証
  └─ 破損時の退避と既定値復旧
        │
        ▼
AppComposition.CreateDefault
        │
        ├─ 各ProviderOptions
        ├─ EventSource / Normalizer
        ├─ EventIngestionPipeline
        └─ ControlWindowViewModel / SettingsEditorViewModel
```

`AppSettings`は設定全体の保存モデルです。主な区分は `ProviderSettings`、`FilterSettings`、`DisplaySettings`、`ObsSettings`、`AudioSettings`、`HistorySettings`、`CompatibilitySettings`、`LogSettings`、`SafetySettings`、`OperationalSettings`です。

## 編集と保存

```text
ControlWindow.xaml
        │ binding
        ▼
SettingsEditorViewModel
        │ ToSettings(baseline)
        ▼
AppSettings
        │
        ▼
ControlWindowViewModel.SaveSettingsAsync
  ├─ Provider設定検証
  ├─ 必要なら受信停止
  ├─ EventSourceとProvider選択を更新
  ├─ Pipeline、Preview、Overlayへ即時反映
  ├─ JsonSettingsStore.SaveAsync
  ├─ OBS再構成
  └─ 必要なら受信再開
```

受信元設定の変更では、EventSourceの接続先とNormalizerの採用条件を同時に更新します。どちらか一方だけを更新すると、「接続しているが採用されない」または「選択外の情報を採用する」状態になります。

`SaveSettingsAsync`は、現在選択中の受信元について、ルーティング、接続先、購読条件、認証内容を実効値で比較します。この比較結果が変わった場合だけ受信を停止して再接続します。表示フィルター、字幕、音声、OBS、履歴、安全設定だけの変更では、現在のAPI接続を維持したまま各機能へ即時反映します。選択されていない受信元の設定変更も、その受信元を選択するまでは再接続を発生させません。

## 設定項目を追加するとき

最低限、次を順番に確認します。

1. `AppSettings.cs`の該当record、既定値、`CurrentSchemaVersion`
2. `JsonSettingsStore`の移行処理と検証条件
3. `SettingsEditorViewModel`のフィールド、プロパティ、コンストラクター、`ToSettings`
4. `ControlWindow.xaml`のbinding
5. `SaveSettingsAsync`で即時反映または再接続が必要か
6. リセットボタンの既定値復元処理
7. 設定、移行、保存、再接続のテスト

既存JSONとの互換性が必要な追加では、単にrecordへプロパティを足すだけで完了しません。古いスキーマからの移行、欠損値、範囲外値、破損JSONの復旧を確認します。

## 秘密情報

- AXISトークン
- DMDATA.JP認証情報
- OBS WebSocketパスワード

これらは平文ログ、診断ZIP、リポジトリ、配布物へ含めません。保存時の保護と表示時の復号は既存のProtectorを通し、`AppSettings`、画面、ログの間で平文を不用意に複製しないでください。

保存済みの平文内容が変わっていない場合は既存のDPAPI暗号文を再利用します。DPAPIは同じ平文でも暗号化のたびに異なる値を生成するため、毎回再暗号化すると設定変更や再接続を誤検出する原因になります。

## 関連テスト

- `tests/EEWTelop.Wpf.Tests/Phase6ViewModelTests.cs`
- `tests/EEWTelop.Wpf.Tests/Phase8PersistenceDiagnosticsTests.cs`
- `tests/EEWTelop.Application.Tests/Phase8PersistenceAndAudioTests.cs`
- 各ProviderのOptionsテスト

受信元変更では、設定保存テストだけでなく、`ProviderSelectionRoutingTests`と各ProviderOptionsテストも実行します。
