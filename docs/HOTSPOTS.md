# 読解負荷が高い箇所

この文書は、大きなクラスを全面的に読み込まず、変更に必要な範囲だけを特定するための案内です。行数は変動するため、責務と入口を基準にします。

## ControlWindowViewModel

対象:

- `src/EEWTelop.Wpf/ViewModels/ControlWindowViewModel.cs`
- `src/EEWTelop.Wpf/ViewModels/ControlWindowViewModel.Operations.cs`

主な責務:

- 接続、切断、再接続と状態表示
- 設定保存と各サービスへの反映
- 受信結果、履歴、過去電文、訓練再表示
- プレビュー、字幕編集、種別別消去
- 音声再生
- OBS Local ViewとOBS WebSocket同期
- ログ、診断、自己確認、テストライブラリ、設定プロファイル

読み方:

- 設定保存は `SaveSettingsAsync` から読む。
- 受信処理は `EventProcessed`の購読先と表示反映処理から読む。
- 音声問題は `PlayEventAudio`周辺だけを読む。
- OBS問題は `ConfigureObsServerAsync`と`Obs`名前空間を先に読む。
- プロファイルやテストライブラリは `.Operations.cs`だけを先に読む。

部分クラスはファイルを分けていますが、状態は同じインスタンスで共有されます。一方のフィールド変更が他方へ影響しないと仮定しないでください。

## JmaXmlEventNormalizer

対象: `src/EEWTelop.Infrastructure.Dmdata/Normalization/JmaXmlEventNormalizer.cs`

主な責務:

- XMLの安全な読み込み
- 電文種別の検出
- EEW、地震、津波、気象、火山、南海トラフの正規化
- XML名前空間に依存しない要素探索
- 地域名、震度、マグニチュード、取消、発表状態の変換

読み方:

1. `Normalize`のswitchで対象電文の入口を特定する。
2. `NormalizeEew`など対象のトップレベルメソッドを読む。
3. そこから呼ばれる `Read...`メソッドだけを追う。
4. 対応する `JmaXmlEventNormalizerTests`のテスト名から期待値を確認する。

XMLヘルパーを変更すると全電文へ影響します。トップレベルの電文処理変更より広いテストが必要です。

## SettingsEditorViewModelとAppSettings

対象:

- `src/EEWTelop.Wpf/ViewModels/SettingsEditorViewModel.cs`
- `src/EEWTelop.Application/Configuration/AppSettings.cs`
- `src/EEWTelop.Infrastructure/Settings/JsonSettingsStore.cs`

設定は保存モデル、編集用ViewModel、JSON移行に分かれています。画面のプロパティだけを追加しても保存されません。詳細は [SETTINGS_FLOW.md](SETTINGS_FLOW.md) を参照してください。

## WeatherWarningPageComposer

対象: `src/EEWTelop.Application/Display/WeatherWarningPageComposer.cs`

同じ `WeatherWarningEvent`から次を生成します。

- 通常の気象警報・注意報
- 警報・注意報の解除
- 記録的短時間大雨情報
- 気象防災速報
- 竜巻注意情報

読み方:

- `Compose`でInformationTypeによる分岐を確認する。
- 通常警報は `CreateActiveWarningPages`、解除は `CreateReleasePages`を読む。
- 竜巻は `CreateTornadoAdvisoryPages`、短時間大雨は `CreateRecordShortDurationHeavyRainPages`を読む。
- ページ分割問題では、Composerのブロック数とWPF/OBSの文字折り返しを分けて確認する。

## PriorityCoordinator

対象: `src/EEWTelop.Application/Coordination/PriorityCoordinator.cs`

このクラスは受信順ではなく、優先度、発表時刻、同一イベント更新、本番・訓練、継続津波、気象イベントの待機状態から表示を決定します。

変更前に、少なくとも次のケースを列挙します。

- 表示中より高い、同じ、低い優先度の新着
- 同一イベントの新しい更新と古い更新
- 取消・解除
- 本番表示中の訓練
- 津波の継続と全解除
- 同優先度の複数気象イベント

`PriorityCoordinatorTests`を仕様書として先に読むと、条件分岐を追いやすくなります。

## ControlWindow.xaml

操作画面全体を含むため、タブやGroupBox名から対象を検索して読みます。binding先は多くが `SettingsEditorViewModel`または`ControlWindowViewModel`です。表示だけの変更でも、保存、リセット、入力範囲、ツールチップの整合を確認します。
