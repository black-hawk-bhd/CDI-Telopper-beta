# CDI-Telopper 2.0.0-beta.77

## デスクトップ表示

- 「出力」→「デスクトップ表示」で、メインディスプレイに字幕を重ねられます。「常に手前に表示」も選べます。字幕に重なった場所でも背後のアプリを操作できます。
- 操作画面左側の「出力」に「デスクトップ表示を消去」を追加しました。受信とOBS送出を続けながら、デスクトップの字幕を消去できます。次の字幕が送出されると表示を再開します。
- 地震・津波・火山・気象のバッジと本文の縦位置を揃え、横型の緊急地震速報では見出しの高さと本文の行間をブラウザープレビューに合わせました。

## 電文の表示と確認

- 緊急地震速報の続報では、新たに警報対象となった地域だけをオレンジ色で表示できます。詳細欄を「推定マグニチュード」と明記し、PLUM法の仮定震源に含まれる固定値 `M1.0` を表示から除外しました。Simulator経由の予想最大震度と、一部の地震電文で震度7の地点が欠落する問題を修正しました。
- 津波警報・注意報の区分が下がった場合、変更を先頭ページに表示します。現在の区分をバッジで表示するか選べます。以前の区分に続く「から切り替えられました。」の文言は「受信前の定型文を編集」で変更できます。
- 「受信・過去電文確認」で、表示フィルターによって非表示になった電文とその理由を確認できます。非表示の電文だけに絞り込めます。外部JMA XMLを選んだときは、取得元の欄からファイルを参照できます。
- P2P地震情報 APIは本番受信専用になりました。旧サンドボックス設定を読み込むと起動時の自動接続がオフになります。過去電文の取得には、気象庁XML、NII、外部JMA XMLファイルを利用できます。出力の「ページ番号を表示」設定は廃止しました。

## Windows x64向けダウンロード

1. **.NET同梱の単一EXE版**。ランタイムを別途インストールせずに起動できます。 [ダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.77/CDI-Telopper-2.0.0-beta.77-win-x64-single-file.zip)
2. **軽量版**。.NET 10 Desktop Runtime（x64、10.0.14以降の10.0系）が必要です。 [ダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.77/CDI-Telopper-2.0.0-beta.77-win-x64-framework-dependent.zip) · [Microsoft公式ダウンロード](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0)

両方のZIPに気象庁のサンプルXML185件を収録しています。「テスト」→「生データ・テストライブラリー」→「XMLフォルダーを一括登録」で `TrainingSamples` を選んでください。 [サンプルの出典](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.77/jmaxml_20261118_CDI-Telopper/README.md)

旧版を通知領域のメニューから終了し、新しいZIPを別のフォルダーに展開してください。実行ファイル横に設定を保存している場合は、旧版の `Settings` を新版の実行ファイルの横にコピーしてから `CDI-Telopper.exe` を起動します。 [操作マニュアル](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.77/docs/USER_GUIDE.md) · [SHA-256](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.77/SHA256SUMS.txt)
