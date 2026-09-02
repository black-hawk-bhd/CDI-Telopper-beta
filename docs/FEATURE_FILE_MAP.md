# 機能別ファイル索引

変更目的から、最初に読む実装とテストを探すための索引です。巨大なViewModelやNormalizerを先頭からすべて読む前に、対象機能の行き先を絞ってください。

## 受信と正規化

| 機能 | 主な実装 | 主なテスト |
| --- | --- | --- |
| 受信元の分類別ルーティング | `Application/Events/RoutedProviderEventSource.cs`、`ProviderSelectionEventNormalizer.cs`、`ProviderRoutingEventNormalizer.cs` | `Application.Tests/ProviderSelectionRoutingTests.cs` |
| P2P通信・再接続・REST復旧 | `Infrastructure.P2P/Transport/P2pEventSource.cs`、`Recovery` | `Infrastructure.P2P.Tests/P2pEventSourceTests.cs`、`P2pRestRecoveryClientTests.cs` |
| P2P JSON正規化 | `Infrastructure.P2P/Normalization/P2pEventNormalizer.cs` | `Infrastructure.P2P.Tests/P2pEventNormalizerTests.cs` |
| DMDATA.JP接続 | `Infrastructure.Dmdata/Transport/DmdataEventSource.cs`、`DmdataSocketApiClient.cs` | `Infrastructure.Dmdata.Tests/DmdataSocketApiClientTests.cs`、`DmdataWebSocketFrameDecoderTests.cs` |
| 気象庁XML正規化 | `Infrastructure.Dmdata/Normalization/JmaXmlEventNormalizer.cs` | `Infrastructure.Dmdata.Tests/JmaXmlEventNormalizerTests.cs` |
| AXIS接続・再接続 | `Infrastructure.Axis/Transport/AxisEventSource.cs` | `Infrastructure.Axis.Tests/AxisRecoveryAndPolicyTests.cs` |
| AXIS形式の復元・正規化 | `Infrastructure.Axis/Normalization/AxisEventNormalizer.cs`と関連Decoder | `Infrastructure.Axis.Tests/AxisEnvelopeDecoderTests.cs` |
| AXISトークン更新 | `Infrastructure.Axis/Security/AxisTokenRefreshService.cs` | `Infrastructure.Axis.Tests/AxisTokenRefreshServiceTests.cs` |
| Wolfx接続・JSON正規化 | `Infrastructure.Wolfx/Transport/WolfxEventSource.cs`、`Normalization/WolfxEventNormalizer.cs` | `Infrastructure.Wolfx.Tests/WolfxEventNormalizerTests.cs` |

表中のパスはすべて `src/EEWTelop.*` または `tests/EEWTelop.*` から始まります。

## 字幕生成

| 情報 | 主な実装 | 主なテスト |
| --- | --- | --- |
| EEW | `Application/Display/EewPageComposer.cs`、`ConcurrentEewProgramComposer.cs` | `Application.Tests/EewAndTsunamiPageComposerTests.cs` |
| 地震 | `Application/Display/QuakePageComposer.cs` | `Application.Tests/QuakePageComposerTests.cs` |
| 津波 | `Application/Display/TsunamiPageComposer.cs`、`Events/TsunamiEventStateAccumulator.cs` | `EewAndTsunamiPageComposerTests.cs`、`TsunamiEventStateAccumulatorTests.cs` |
| 気象警報・注意報・解除 | `Application/Display/WeatherWarningPageComposer.cs` | `Application.Tests/WeatherWarningPageComposerTests.cs` |
| 竜巻・記録的短時間大雨・気象防災速報 | `Application/Display/WeatherWarningPageComposer.cs` | `Application.Tests/WeatherWarningPageComposerTests.cs` |
| 火山 | `Application/Display/VolcanoPageComposer.cs` | `Application.Tests/VolcanoPageComposerTests.cs` |
| 字幕定型文の上書き | `Application/Display/SubtitlePhraseCatalog.cs` | `Wpf.Tests/SubtitlePhraseTemplateViewModelTests.cs` |

## 表示制御と出力

| 機能 | 主な実装 | 主なテスト |
| --- | --- | --- |
| 優先度、割り込み、更新、待機 | `Application/Coordination/PriorityCoordinator.cs` | `Application.Tests/PriorityCoordinatorTests.cs` |
| 本番情報の繰り返し表示 | `Application/Coordination/ProductionReplayCatalog.cs` | `Application.Tests/ProductionReplayCatalogTests.cs` |
| ページ時刻 | `Application/Coordination/PageClock.cs` | `Application.Tests/PageClockTests.cs` |
| OBSローカル配信 | `Wpf/Obs/ObsLocalViewServer.cs`、`ObsViewSnapshot.cs` | `Wpf.Tests/Phase7ObsLocalViewTests.cs` |
| OBSブラウザーソース登録 | `Wpf/Obs/ObsBrowserSourceSynchronizer.cs` | `Wpf.Tests/Phase7ObsLocalViewTests.cs` |
| プレビューと字幕編集 | `Wpf/PreviewWindow*`、`SubtitleEditor*` | `Wpf.Tests/SubtitleEditorViewModelTests.cs` |
| 受信・過去電文確認 | `Wpf/TelegramReviewWindow*`、`ViewModels/ReceivedTelegramViewModel.cs` | `Wpf.Tests/Phase6ViewModelTests.cs` |
| 音声判定 | `Application/Audio/AudioPolicy.cs`と`ControlWindowViewModel`の音声処理 | `Application.Tests/Phase8PersistenceAndAudioTests.cs`、`Wpf.Tests/Phase6ViewModelTests.cs` |

## 設定、保存、診断

| 機能 | 主な実装 | 主なテスト |
| --- | --- | --- |
| 設定モデルと既定値 | `Application/Configuration/AppSettings.cs` | `Application.Tests/Phase8PersistenceAndAudioTests.cs` |
| 設定画面との変換 | `Wpf/ViewModels/SettingsEditorViewModel.cs` | `Wpf.Tests/Phase6ViewModelTests.cs` |
| JSON移行・検証・保存 | `Infrastructure/Settings/JsonSettingsStore.cs` | `Wpf.Tests/Phase8PersistenceDiagnosticsTests.cs` |
| 生電文保存 | `Infrastructure/Operations/OperationalStores.cs` | `Wpf.Tests/RawProviderMessageArchiveTests.cs` |
| 診断ZIP | `Infrastructure/Diagnostics` | `Wpf.Tests/Phase8PersistenceDiagnosticsTests.cs` |
| 操作ログと自己診断 | `Application/Operations`、`Wpf/Services/OperationalSelfCheckService.cs` | `Wpf.Tests/OperationalFeaturesTests.cs` |

## UIの入口

- アプリ起動とタスクトレイ: `Wpf/App.xaml.cs`
- 依存関係の構成: `Wpf/Bootstrap/AppComposition.cs`
- 操作画面レイアウト: `Wpf/ControlWindow.xaml`
- 操作画面の状態とコマンド: `Wpf/ViewModels/ControlWindowViewModel.cs`
- プロファイル、テストライブラリ、自己診断: `ControlWindowViewModel.Operations.cs`
- 設定項目: `Wpf/ViewModels/SettingsEditorViewModel.cs`

操作画面の変更では、XAMLだけでなくViewModel、設定モデル、JSON移行、設定テストの4点を確認します。
