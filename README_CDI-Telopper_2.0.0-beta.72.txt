CDI-Telopper 2.0.0-beta.72 操作ガイド
Windows 10 / Windows 11（64ビット）

旧版を終了し、新しいZIPを別のフォルダーへ展開して CDI-Telopper.exe を起動してください。
フォルダー版と単一EXE版は .NET 10.0.14 を同梱します。

【地震情報の字幕】
震度バッジと地名の1行目の高さを揃えました。
同じ電文の中で重複する津波案内や補足文は1回表示します。

【Wolfxの接続とログ】
津波・特別地震情報はWebSocketで受信します。地震情報にWolfxを選ぶと、WebSocketの概要と地震詳細HTTPを使います。
再接続やHTTP取得失敗の警告には、理由と次の試行までの待ち時間を表示します。

【南海トラフの定例解説】
テロップに出すには「フィルター」→「南海トラフの定例解説をテロップに表示」をオンにし、「保存して反映」を押します。初期値はオフです。
ブラウザープレビューの「受信済み電文の個別確認」では、確認一覧での表示を個別に設定できます。

【操作マニュアル・利用規約】
日本語 https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.72/docs/USER_GUIDE.md
English https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.72/docs/USER_GUIDE_EN.md
外部APIの設定は同梱の EXTERNAL_API_V1.md を参照してください。
利用規約と免責事項は同梱の TERMS.md を参照してください。
防災判断には気象庁などが発表する公式情報も確認してください。
