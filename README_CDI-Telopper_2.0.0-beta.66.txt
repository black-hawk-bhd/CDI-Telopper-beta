CDI-Telopper 2.0.0-beta.66 操作ガイド
Windows 10 / Windows 11（64ビット）

旧版を終了し、新しいZIPを別のフォルダーへ展開して CDI-Telopper.exe を起動してください。
フォルダー版と単一EXE版は、いずれも .NET 10.0.14 を同梱します。
通常の更新に設定の初期化は不要です。

【外部APIを起動時に使う】
「出力」→「OBS Local View」を有効にし、「CDI External API v1」で
「起動時に外部APIを有効化」をONにして「保存して反映」を押します。初期値はOFFです。

【接続URLを再起動後も使う】
「CDI External API v1」の接続ポートに未使用の番号を入力して「保存して反映」を押します。
ポートはOBS Local Viewと共用です。0では起動時に空きポートが自動で割り当てられます。
「接続キーを保存」を押すと、現在のWindowsユーザー用にキーを暗号化して保存します。
APIを有効にして「外部API 接続URLをコピー」を押し、連携先にURLを登録してください。
キーを再発行した場合やポートを変更した場合は、新しいURLを連携先に登録してください。
詳細は同梱の EXTERNAL_API_V1.md を参照してください。

【詳細マニュアル・利用規約】
https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.64/README_CDI-Telopper_2.0.0-beta.64.txt
利用規約と免責事項は同梱の TERMS.md を参照してください。
本ソフトウェアだけを防災判断の根拠にせず、気象庁などの公式情報も確認してください。
