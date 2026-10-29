# .NET 10移行と検証

2026年10月29日。beta.63で全15プロジェクトを.NET 10へ移行した。
公開するフォルダー版・単一EXE版は、いずれも.NET 10.0.14を同梱する。

## 実行環境とビルド

- ランタイム最低版: **10.0.14**。SDK: **10.0.403**。
  [公式のリリース情報](https://raw.githubusercontent.com/dotnet/core/main/release-notes/releases-index.json)で確認した。
- 共有層は `net10.0`、WPF・DMDATA・AXISとWindows用テストは `net10.0-windows`。
  C# 12、Analyzer、`TreatWarningsAsErrors` を維持した。
- `global.json` のSDK指定と、`scripts/verify.ps1` / `scripts/publish.ps1` の
  自動選択先を `.dotnet-sdk/dotnet.exe` に統一した。`-DotnetPath` による明示指定も使える。
- WPFの実行環境指定、自己完結型の復元・発行、実際に解決されたパックと
  パッケージ内バージョンの検査を10.0.14へ更新した。旧版や.NET 8のパック混入を拒否する。
- [Microsoft.Testing.Platformの.NET 10対応方式](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test)
  を `global.json` で選択した。`dotnet test --project <csproj>` を使用する。
  個別のMSTest絞り込みは `--filter` を指定する。

## 互換性への対応

[.NET 9](https://learn.microsoft.com/en-us/dotnet/core/compatibility/9.0)・
[.NET 10](https://learn.microsoft.com/en-us/dotnet/core/compatibility/10)の互換性情報を確認し、
実際に出た解析指摘をコード側で解消した。

- 配列を渡している内部ヘルパーは配列型と `Length` を使い、ZIP一覧は実際の戻り値型を保持する。
- シミュレーターJSON配列のヘルパーは `JsonElement.ArrayEnumerator` を返す。
  配列がない場合の空列挙と本番・訓練の区分は既存のテストで確認した。
- .NET 10のZipArchiveは不正な件数情報を以前より早く拒否する。
  改ざんされた件数で失敗するケースに加え、正しい件数でも管理情報が2 MiBを超えると
  既存の読み込み上限で拒否するケースを追加した。
- Brotliを有効化せず、gzip/deflate・本文容量制限・期限・初期同期の安全対策を維持した。
  新しいライブラリーや別デコーダーへの置換は行っていない。

震度5弱と「震度5弱以上 未入電」、観測点末尾の全角＊の表示処理、取消・報番号逆転、
訓練・リハーサルと本番の分離、API初期同期の字幕・音声非再実行は既存仕様を維持した。

## 検証結果

- 狙ったテスト: **62件成功**（Brotli、API初期同期、ZIP、シミュレーター）。
- 全自動テスト: **1,096件成功、スキップ0**。
  Domain 2 / Application 413 / P2P 49 / DMDATA 224 / AXIS 62 / Wolfx 10 / WPF 336。
- Releaseビルド: 警告0・エラー0。ランタイム配布ガード: **7件成功**。
- Chromeによる字幕描画: **13項目成功**。
- Windows PowerShell 5.1でフォルダー版・単一EXE版を発行し、起動確認も成功した。
  実際のCore/WindowsDesktop同梱版はいずれも10.0.14。存在しない.NET導入先を
  DOTNET_ROOTに指定しても両形式が起動し、ホストの記録でも10.0.14の自己完結型動作を確認した。

ログはローカルの `artifacts/validation/net10-migration/` に保存する。
ソースビルドは `scripts/verify.ps1`、配布物生成は `scripts/publish.ps1` を使用する。
既存の配布ディレクトリーを上書きせず、ローカル検証には `-OutputLabel` を指定する。

## 公開前の確認

実受信・実OBSの目視と音声、運用環境での最終確認は未実施。
設定や保存先・外部APIの形式は変更していない。
配布する場合は、新しいソースから発行した両形式のZIPと同梱版・ハッシュを照合して公開する。
旧版EXEはシステム側の.NET更新だけでは新しいランタイムへ変わらない。

[サポート期間](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)は
.NET 10 LTSが2028年11月14日まで。最新の修正版を継続して利用する。
