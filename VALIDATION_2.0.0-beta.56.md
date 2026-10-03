# beta.56 公開前検証

実施日：2026年10月4日。Windows / .NET SDK 8.0.423 / .NET 8 / C# 12。

## 点検範囲

Domain、Application、共通Infrastructure、P2P、AXIS、Wolfx、DMDATA（OAuth・JMA XML/PULLを含む）、WPF、OBS Local View、外部API、Simulator、設定・保存・終了処理を静的に点検しました。今回追加した契約情報と4タブ分離、および点検で修正した経路を回帰テストで検証しています。全ての状況や無欠陥を保証するものではありません。

## 実行結果

関連プロジェクトのテストを先に実行し、その後 `scripts/verify.ps1` で全自動テストを実行しました。

| プロジェクト | 合格 | 不合格・スキップ |
| --- | ---: | ---: |
| Domain | 2 | 0 |
| Application | 258 | 0 |
| Infrastructure.P2P | 49 | 0 |
| Infrastructure.Dmdata | 205 | 0 |
| Infrastructure.Axis | 44 | 0 |
| Infrastructure.Wolfx | 10 | 0 |
| Wpf | 235 | 0 |
| 合計 | 803 | 0 |

- 全Releaseビルドは警告0件・エラー0件。`TreatWarningsAsErrors` とAnalyzerは無効化していません。
- `dotnet list EEWTelop.sln package --vulnerable --include-transitive --source https://api.nuget.org/v3/index.json`：公式NuGetフィードによる確認で公表済みの脆弱な依存パッケージは検出されませんでした。未知の脆弱性やアプリ全体の安全性を保証しません。
- Git差分の空白エラーなし。公開対象のソース・テスト・文書で既知の秘密鍵／トークン形式の簡易確認を実施し、該当はありませんでした。公開OAuthクライアントIDは秘密トークンとは異なります。

## 主な回帰検証

- 不正なP2P/Wolfx配列要素をInvalidとして扱い、続く正常電文を処理できること。
- 全対応30種類のJMA電文について、Control.Statusの訓練・試験を本番から分離し、原文と明示的な非本番SourceModeを保持すること。
- 訓練と本番の署名・報数・EEWプロバイダー横断重複判定・JMA自動代替のfingerprint分離と保存往復。旧保存署名の未記載値はProduction / IsTest=falseとして復元する互換制約があります。
- 一部解除と発表・継続が同居する気象電文で各地域の状態を保持し、明示的な取消は維持すること。
- 未保存のSandbox選択でも本番中の訓練確認を回避できないこと。
- OAuth中のプロファイル適用禁止と、プロファイル読込み待機中にOAuthが始まった場合の設定非置換・非保存。
- 破損保存JSON内のnull要素をバックアップし、空状態で復旧すること。正常状態・正当な空状態は変更しないこと。
- 契約取得の認証・401時の一度だけの再試行・権限不足・APIエラー・不正応答・取得中止・古いアカウントの応答破棄・画面状態の区別。
- タブ順序、各グループの配置、既存設定・コマンドのBindingとPasswordChangedハンドラーを維持すること。

## 未確認事項

- 共通DMDATA OAuthクライアント登録側の `contract.list` 許可状態。
- 実アカウントによる契約情報取得・OAuth追加認可・実受信。
- 実OBS合成・音声・新画面の目視確認。

契約取得の権限不足は未契約を意味しません。OAuth利用では登録側の許可と利用者の追加認可が必要です。配信前に利用環境で確認してください。
