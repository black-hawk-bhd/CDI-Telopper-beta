# CDI-Telopper 2.0.0-beta.75

- OBSとブラウザープレビューで、情報種別ごとに透過PNGを字幕に添えられます。「出力」で画像、位置、横幅を設定できます。
- 訓練ライブラリーで、サブフォルダーを含むXMLを一括登録できます。登録したケースは情報種別・電文コードで絞り込めます。
- 気象庁のサンプルXMLを185件収録しました。ZIPを展開し、「テスト」→「生データ・テストライブラリー」→「XMLフォルダーを一括登録」で `TrainingSamples` フォルダーを選びます。 [サンプルの出典](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.75/jmaxml_20261118_CDI-Telopper/README.md)

## Windows x64向けダウンロード

1. **.NET同梱の単一EXE版**。追加のランタイムを入れずに起動できます。 [ダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.75/CDI-Telopper-2.0.0-beta.75-win-x64-single-file.zip)
2. **軽量版**。.NET 10 Desktop Runtime（x64、10.0.14以降の10.0系）が必要です。 [ダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.75/CDI-Telopper-2.0.0-beta.75-win-x64-framework-dependent.zip) ・ [Microsoft公式ダウンロード](https://dotnet.microsoft.com/ja-jp/download/dotnet/10.0)

旧版を通知領域のメニューから終了し、新しいZIPを別のフォルダーへ展開して `CDI-Telopper.exe` を起動してください。実行ファイル横に設定を保存している場合は、旧版の `Settings` を新版の実行ファイルの横へコピーします。 [操作マニュアル](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/v2.0.0-beta.75/docs/USER_GUIDE.md) ・ [SHA-256](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/download/v2.0.0-beta.75/SHA256SUMS.txt)
