# CDI-Telopper 2.0.0-beta.63

2026年10月29日。.NET 10への移行、Brotli対策、縦型EEWの左右配置を含む更新です。Windows x64向けの自己完結型フォルダー版・単一EXE版に.NET 10.0.14を同梱します。通常利用に.NET SDKやVisual Studioは不要です。音声ファイルは同梱していません。

## 変更内容

- 本体・受信プロバイダー・テストの全15プロジェクトを.NET 10へ移行しました。ソースビルドはSDK 10.0.403を使用します。Analyzerと警告のエラー扱いを維持し、新SDKの指摘をコード側で解消しました。
- [CVE-2026-111497](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9)に対応したランタイムへ更新しました。津波初期取得のBrotliを無効化し、gzip/deflateを維持します。未対応・複合の圧縮形式は本文を開く前に拒否します。容量・期限の制限と、初期同期で字幕・音声を再実行しない仕様を維持します。
- 自己完結型の復元と発行の条件を統一しました。指定値と実際に解決されたランタイムの旧版を拒否し、両配布形式の同梱版を検査して各パッケージの`version.json`へ記録します。
- 「表示」→「ページと文字」に「縦型EEWを左揃え（OFFは右揃え）」を追加しました。縦型をONにして選択し、「保存して反映」で有効になります。初期値はOFF（右揃え）です。表示中の警報にも反映し、ページ番号は反対側へ配置します。帯の内部・色・フォント・2件の並び順・横型表示は維持します。
- .NET 10のテスト実行方式へ移行しました。不正ZIPの早期拒否と管理情報の読み込み上限を別々に検証し、従来のZIP安全対策を維持します。

## 検証と注意

- 全1,096自動テスト、配布ガード7件、Chrome描画13項目が成功。ビルド警告0・エラー0です。[検証結果](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.63/VALIDATION_2.0.0-beta.63.md) / [.NET 10移行記録](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.63/docs/dotnet10-migration.md) / [縦型の操作手順](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.63/docs/eew-vertical-style.md)。
- 両配布形式は独立した設定で起動確認し、版・コミット・同梱ランタイム・ZIPの内容・SHA-256を照合します。
- 実受信・実OBSの目視と音声確認は未実施です。導入環境で本番前に確認してください。自動テストや起動確認は実配信の確認を代替しません。
- 設定の保存先、震度5弱と「震度5弱以上 未入電」の区別、元の地点名・電文、本番/訓練の区分を維持します。既存の利用規約・外部API利用条件・非商用条件・免責事項は変更しません。

## 配布物・更新方法

- `CDI-Telopper-2.0.0-beta.63-win-x64-folder.zip`
- `CDI-Telopper-2.0.0-beta.63-win-x64-single-file.zip`
- `version.json`
- `SHA256SUMS.txt`

旧版を終了し、設定と必要な履歴をバックアップして、新しいZIPを通常の新規フォルダーへ完全に展開してください。設定の初期化は不要です。**旧EXEの同梱.NETは、PC側の.NET更新だけでは置き換わりません。** 新しい配布物で更新してください。
