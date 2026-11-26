# CDI-Telopper 2.0.0-beta.76

## 緊急地震速報と字幕

- 「表示」→「ページと文字」で、予想最大震度と推定マグニチュードを表示できるようになりました。初期値はオフです。有効時は受信データにある項目を「予想最大震度」「推定M」と明記し、OBSとプレビューの横型・縦型に表示します。取消時は該当する速報の数値を消し、取消表示へ切り替えます。
- 地震・津波・火山の字幕で、バッジと本文の先頭行の縦位置を揃えました。長い地震情報や河川洪水予報の本文は文末の「。」を優先して改ページします。遠地地震の海外津波観測は1地点1行、最大2地点ずつ表示します。
- 南海トラフ地震の情報と北海道・三陸沖後発地震注意情報の表示を個別に切り替えられます。気象庁XMLとWolfxでの受信設定には、北海道・三陸沖後発地震注意情報も併記しました。

## 音声・訓練・操作画面

- 「音声」→「火山情報」で噴火警報・予報（VFVO50）と噴火速報（VFVO56）の通知音を個別に設定できます。比較用テロップを繰り返したときの音声再生判定を修正し、再生しなかった理由をログで確認できるようにしました。
- Disaster Simulatorで、震度速報と津波注意報・警報のイベントIDが一致する場合、震度速報の定型注意文の省略と震度速報の非表示が訓練にも適用されます。非表示を有効にすると、後から津波注意報・警報が届いた場合も該当する震度速報を取り下げます。
- 一部の津波情報（VTSE41）、指定河川洪水予報、気象警報・注意報（VPWW61）の訓練ページ生成を修正しました。テストライブラリーはXML・JSONのファイル登録とXMLフォルダー一括登録に対応します。dmdata一式の登録、訓練ケースのZIP入出力、参考PNGの添付を廃止しました。
- 電文確認画面の区分バッジを非表示にする設定を廃止しました。画面の略称を「緊急地震速報」に統一し、設定欄の最大幅を揃えました。OBS用URLのコピーボタンは「地震・火山スーパー」「津波スーパー」「気象スーパー」の表記に変更しました。

## Windows x64向けダウンロード

1. **.NET同梱の単一EXE版**。追加のランタイムを入れずに起動できます。 [ダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.76/CDI-Telopper-2.0.0-beta.76-win-x64-single-file.zip)
2. **軽量版**。.NET 10 Desktop Runtime（x64、10.0.14以降の10.0系）が必要です。 [ダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.76/CDI-Telopper-2.0.0-beta.76-win-x64-framework-dependent.zip) · [Microsoft公式ダウンロード](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0)

両方のZIPに気象庁のサンプルXML185件を収録しています。「テスト」→「生データ・テストライブラリー」→「XMLフォルダーを一括登録」で `TrainingSamples` フォルダーを選んでください。 [サンプルの出典](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.76/jmaxml_20261118_CDI-Telopper/README.md)

旧版を通知領域のメニューから終了し、新しいZIPを別のフォルダーに展開してください。実行ファイル横に設定を保存している場合は、旧版の `Settings` フォルダーを新版の実行ファイルの横へコピーした後で `CDI-Telopper.exe` を起動します。 [操作マニュアル](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.76/docs/USER_GUIDE.md) · [SHA-256](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.76/SHA256SUMS.txt)
