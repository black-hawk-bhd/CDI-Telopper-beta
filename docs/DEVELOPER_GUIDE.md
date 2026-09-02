# CDI-Telopper 開発者ガイド

この文書は、初めてコードを読む開発者が、変更目的から必要な実装とテストへ短時間で到達するための入口です。利用方法と配布手順はルートの `README.md` と `SOURCE_BUILD.md` を参照してください。

## 最初に読む順番

1. [ARCHITECTURE.md](ARCHITECTURE.md) — プロジェクト境界と受信からOBS出力までの処理経路
2. [FEATURE_FILE_MAP.md](FEATURE_FILE_MAP.md) — 機能から実装ファイルとテストを探す索引
3. [TELEGRAM_SUPPORT_MATRIX.md](TELEGRAM_SUPPORT_MATRIX.md) — 電文コード、ドメインイベント、字幕生成の対応
4. [SETTINGS_FLOW.md](SETTINGS_FLOW.md) — 設定の読み込み、編集、保存、再接続
5. [SAFE_CHANGE_GUIDE.md](SAFE_CHANGE_GUIDE.md) — 変更時の確認順序とテスト
6. [HOTSPOTS.md](HOTSPOTS.md) — 大きなクラスを読むときの範囲の絞り方

## リポジトリの基本構造

| 場所 | 役割 |
| --- | --- |
| `src/EEWTelop.Domain` | プロバイダーや画面に依存しない災害イベントと値 |
| `src/EEWTelop.Application` | 正規化後の処理、表示ページ生成、優先度制御、設定モデル |
| `src/EEWTelop.Infrastructure` | JSON設定、ログ、永続化、診断などの共通実装 |
| `src/EEWTelop.Infrastructure.P2P` | P2P地震情報APIの通信、JSON検証、正規化、復旧 |
| `src/EEWTelop.Infrastructure.Dmdata` | DMDATA.JP通信、気象庁XMLの安全な解析、履歴取得 |
| `src/EEWTelop.Infrastructure.Axis` | AXIS通信、専用EEW JSON、JMA JSONからXMLへの復元、トークン更新 |
| `src/EEWTelop.Infrastructure.Wolfx` | WolfxのEEW・地震情報WebSocket通信、ハートビート、JSON正規化 |
| `src/EEWTelop.Wpf` | アプリ起動、依存関係の構成、操作画面、プレビュー、OBS連携、音声 |
| `tests` | 上記各プロジェクトに対応する自動テスト |
| `fixtures` | 公開可能な最小限のテスト入力 |

内部のプロジェクト名と名前空間は、旧版からの継続性のため `EEWTelop.*` のままです。製品名と実行ファイル名は CDI-Telopper です。

## 変更箇所を探す最短経路

1. 入力形式の問題なら、該当プロバイダーの `Normalization` または `Transport` を確認します。
2. 正規化後の値が違うなら、`EEWTelop.Domain/Events` と各Normalizerを確認します。
3. 字幕の文言、バッジ、ページ数なら、`EEWTelop.Application/Display` の対象Composerを確認します。
4. 表示順、割り込み、継続表示、解除なら、`PriorityCoordinator` と状態蓄積処理を確認します。
5. 操作画面、保存、再接続、OBS、音声なら、WPF層を確認します。
6. 実装を読む前に [FEATURE_FILE_MAP.md](FEATURE_FILE_MAP.md) から対応テストを特定します。

## 重要な境界

- プロバイダー固有のJSON・XML・認証・再接続処理をDomainやComposerへ持ち込みません。
- Normalizerは外部形式を共通の `DisasterEvent` 派生型へ変換します。
- Composerは共通イベントから `DisplayProgram` を作り、受信元を理由に表示を分岐させません。
- `PriorityCoordinator` は表示優先度と継続状態を管理し、電文の解析は行いません。
- WPFは画面と外部出力を担当し、電文構造を直接解釈しません。

## コメントの方針

コードコメントは「何をしているか」ではなく、コードだけでは分からない「なぜ必要か」を記載します。

コメントを追加する対象:

- 気象庁電文やプロバイダーごとの例外
- 非同期処理、再接続、状態保持の順序制約
- 一見削除できそうだが、互換性や安全性のため必要な処理
- 表示行数やグルーピングなど、運用上の不変条件

変数名やメソッド名を言い換えるだけのコメント、長い仕様書の転載、将来の予定だけを記したコメントは追加しません。長い説明は `docs` に置きます。

## 検証

すべての変更後に、リポジトリのルートから次を実行します。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

このスクリプトは復元、Releaseビルド、自動テストをまとめて実行します。個別テストだけで完了とせず、最終確認では必ず全体検証を行います。
