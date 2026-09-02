# 安全に変更するための確認手順

## 基本手順

1. [FEATURE_FILE_MAP.md](FEATURE_FILE_MAP.md)で対象実装とテストを特定する。
2. 入力、共通イベント、字幕、表示制御、出力のどの段階の問題かを切り分ける。
3. 既存テストに、修正前に失敗する最小の再現ケースを追加する。
4. 対象段階だけを修正する。
5. 対象プロジェクトのテストを実行する。
6. `scripts\verify.ps1`で全体を検証する。
7. 仕様を変えた場合はREADMEと開発者文書を更新する。

## 問題別の確認順序

### 電文を受信しない

1. 情報分類で「受信しない」が選ばれていないか。
2. `RoutedProviderEventSource`が対象Providerへ接続する構成か。
3. ProviderOptionsが必要な契約区分・チャンネル・電文型を要求しているか。
4. Transportがフレームを `RawProviderMessage`へ変換しているか。
5. NormalizerがProvider名とContentFormatを受理しているか。

### 受信ログにはあるが字幕が出ない

1. Normalizerの結果がSuccess、Ignored、Invalidのどれか。
2. `ProviderSelectionEventNormalizer`で選択外になっていないか。
3. `EventVersionCache`で重複または古い更新と判定されていないか。
4. `EventDisplayFilter`で非表示になっていないか。
5. Composerが空のページを生成していないか。
6. `PriorityCoordinator`で低優先度、訓練、本番状態により待機・抑止されていないか。

### 字幕の内容、地域、バッジ、ページ数が違う

1. Normalizerテストで共通イベントの値を確認する。
2. 値が正しければ対象PageComposerだけを確認する。
3. 画面幅による折り返しと、Composerが生成するブロック数を区別する。
4. 取消・解除は、対象地域、種別、発表状態、イベントIDを確認する。
5. 通常、続報、解除、全解除のテストを分ける。

### 再接続時に異常終了する

1. StopとDisposeを区別する。
2. 既存の受信ループが終了する前に新しいループを開始していないか。
3. CancellationToken、WebSocket、HTTPレスポンスが二重解放されていないか。
4. プロバイダー単体の失敗と集約接続状態を混同していないか。
5. 手動切断、通信断、設定保存による再接続、終了処理を別々にテストする。

### 音が鳴らない、重複して鳴る

1. 共通イベントの種別、取消、最大震度、警報レベルを確認する。
2. `AudioPolicy`の選択結果を確認する。
3. 音声ファイル設定と有効状態を確認する。
4. 同じイベントの更新・再表示で再生すべきかを確認する。
5. OBSでは音声対象が「地震字幕・全ての音声」だけであることを確認する。

## 変更してはいけない境界

- Provider固有形式をComposerで直接読む。
- WPFからJSONやXMLの電文構造を直接参照する。
- NormalizerでOBSや画面状態を変更する。
- 表示文言をTransportに持たせる。
- テストを通すためだけに、対応外電文をSuccessとして返す。
- 秘密情報をログ、例外メッセージ、テスト成果物へ書き出す。

## 最終検証

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

全体検証の成功に加え、変更した情報種別について、通常、更新、取消または解除、破損入力、選択外Providerを確認します。配布物を作る場合は、全体検証成功後に `scripts\publish.ps1` を使用します。
