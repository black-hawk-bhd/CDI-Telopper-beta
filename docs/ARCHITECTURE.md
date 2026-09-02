# アーキテクチャ

## 目的

CDI-Telopperは、複数プロバイダーの異なる形式を共通の災害イベントへ変換し、同じ表示・音声・OBS処理へ渡します。受信元の違いを字幕生成まで引きずらないことが、中心となる設計方針です。

## 依存方向

```text
EEWTelop.Wpf
  ├─ EEWTelop.Infrastructure
  ├─ EEWTelop.Infrastructure.P2P
  ├─ EEWTelop.Infrastructure.Dmdata
  ├─ EEWTelop.Infrastructure.Axis
  ├─ EEWTelop.Infrastructure.Wolfx
  └─ EEWTelop.Application
         └─ EEWTelop.Domain

各Infrastructureプロジェクト
  ├─ EEWTelop.Application
  └─ EEWTelop.Domain
```

Domainはほかのプロジェクトへ依存しません。ApplicationはDomainだけに依存します。具体的な通信、ファイル、WPFへの依存は外側の層に置きます。

## 実行時の処理経路

```text
P2P / DMDATA.JP / AXIS / Wolfx
        │
        ▼
IEventSource ── RawProviderMessage
        │
        ▼
ProviderRoutingEventNormalizer
ProviderSelectionEventNormalizer
        │
        ▼
P2pEventNormalizer / JmaXmlEventNormalizer / AxisEventNormalizer / WolfxEventNormalizer
        │
        ▼
DisasterEvent
        │
        ▼
EventIngestionPipeline
  ├─ 重複・古い更新の判定
  ├─ 津波状態の統合
  ├─ 表示フィルター
  ├─ PageComposer
  └─ PriorityCoordinator
        │
        ▼
ControlWindowViewModel
  ├─ プレビュー
  ├─ 音声
  ├─ 受信・過去電文確認
  ├─ 状態保存
  └─ OBS Local View
```

依存関係の組み立ては `src/EEWTelop.Wpf/Bootstrap/AppComposition.cs` に集約されています。新しい実装を追加したのに動作経路へ入らない場合は、まずここで登録されているか確認します。

## 共通イベント

| イベント | 主な入力 | 主なComposer |
| --- | --- | --- |
| `EewEvent` | P2P 556、VXSE43、警報を含むVXSE45、AXIS `eew`、Wolfx JMA EEW | `EewPageComposer` |
| `QuakeEvent` | P2P 551、VXSE51/52/53/62、VYSE50/60、Wolfx JMA地震情報 | `QuakePageComposer` |
| `TsunamiEvent` | P2P 552、VTSE41/51/52 | `TsunamiPageComposer` |
| `WeatherWarningEvent` | VPWW、VPOA、VPBS、VPHW | `WeatherWarningPageComposer` |
| `VolcanoEvent` | VFVO50/56 | `VolcanoPageComposer` |

`PageComposer`がイベント型による最終的な振り分けを行い、字幕の置換設定を適用します。

## 状態を持つ主要コンポーネント

| コンポーネント | 保持する状態 | 変更時の注意 |
| --- | --- | --- |
| `EventVersionCache` | 受理済みイベント署名 | 重複、続報、古い電文の扱いに影響 |
| `TsunamiEventStateAccumulator` | 津波区域の継続状態 | 部分更新を単独電文として扱わない |
| `ConcurrentEewProgramComposer` | 同時発生EEWの表示状態 | EEW消去時に状態も消去する |
| `PriorityCoordinator` | 表示中、待機中、継続津波、気象イベント | 優先度、更新、取消、訓練抑止に影響 |
| `ProductionReplayCatalog` | 再表示対象の本番情報 | 解除、取消、期限切れで除外する |
| `JsonDisplayStateStore` | 再起動後に復元する表示状態 | 保存形式変更では移行と復元を確認 |

## 受信元の選択

設定ではEEW、地震、津波、気象、火山、南海トラフごとに受信元を選択します。`RoutedProviderEventSource`は選択された分類に必要なプロバイダーだけを動作させ、`ProviderSelectionEventNormalizer`は正規化後に選択外のイベントを除外します。

「APIへ接続するか」と「受信したイベントを採用するか」は別の防御層です。片方だけを変更すると、不要な接続または選択外電文の表示が発生する可能性があります。

## 出力

`PriorityCoordinator`のスナップショットが画面状態の基準です。WPFプレビューとOBS Local Viewは同じ表示状態から出力します。OBS用HTMLとJavaScriptは `src/EEWTelop.Wpf/Obs/Assets` にあり、サーバー処理は `ObsLocalViewServer`、表示用スナップショットは `ObsViewSnapshot`が担当します。

音声は字幕ページの単なる副作用ではなく、正規化されたイベントと音声ポリシーに基づいて制御されます。OBSでは「地震字幕・全ての音声」だけを音声ミキサー対象にします。
