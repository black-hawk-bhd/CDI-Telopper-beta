# CDI-Telopper

**Comprehensive Disaster Information Telopper（CDI-Telopper）2.0.0-beta.32**は、P2P地震情報、DMDATA.JP、AXISから
防災情報を受信し、画面およびOBS向け字幕へ出力するWindowsアプリです。
現在の配布系統は、各プロバイダーと拡張機能を含む統合ベータ版です。

音声通知は、地震情報の最低震度と津波情報の最低発表区分を設定画面の
プルダウンから選択できます。手動切断時には確認画面を表示します。

津波情報は大津波警報、津波警報、津波注意報の順でページを分離して優先表示します。
0.2m級の津波予報は既定で非表示です。「表示・出力」の設定を有効にした場合だけ
「津波予報（若干の海面変動）」として表示します。

OBS WebSocket v5を有効にすると、用途別のブラウザーソース3件を現在のOBS
シーンへ自動作成し、CDI-Telopperの起動ごとに変わるURLも自動更新します。
自動消去時間は、緊急地震速報・地震情報・津波情報ごとに設定できます。

1.3.1では、異なるイベントIDの緊急地震速報（警報）を最大2件まで上下表示できます。
続報は同じ表示枠を更新し、取消報は対象枠だけを取消表示へ切り替えます。
5秒差の同時発表、1件取消、2件取消を確認する時系列訓練シナリオも収録しています。

1.3.2では、緊急地震速報の発表地域が多い場合に、気象庁の府県予報区を
地方名へ集約して表示します。第1報から第15報まで対象地域が段階的に拡大する
訓練シナリオも追加しました。Scenario Studioは構想段階のため含まれません。
地震・津波地図機能は廃止し、関連する操作画面・OBS出力・配布資産を本体から除外しました。

OBS Local Viewの更新間隔は50～1000ミリ秒で設定できます。
既定値は従来と同じ1000ミリ秒です。OBS音声モニター方式は「OBS出力のみ」に
固定し、PC側のモニター機器へ音声を返す旧設定は読み込み時に無効化します。

履歴リハーサルでは、NII 気象庁防災情報XMLデータベースから過去の地震・津波XMLを
手動取得できます。この取得元は明示的に選択した場合だけ利用され、1回20電文まで、
リクエスト間隔1秒以上で取得し、XMLをローカルキャッシュへ保存します。
出典は`docs/data-sources.md`を参照してください。

PC画面へ字幕を常時重ねる旧オーバーレイは廃止しました。操作画面右上の
「受信・過去電文を確認」から独立ウインドウを開き、本番受信した電文と設定済みの
履歴元から取得した過去電文を、ページ単位の本文まで確認できます。確認だけでは
OBSへ送出されず、「選択電文を訓練再表示」を押した場合だけ訓練表示します。

Start the Release application, select a
scenario under **テスト**, and choose **プレビューへ表示**. Copy the generated
loopback-only URL with **OBS URLをコピー** and use it as a 1920×1080 OBS Browser
Source. Test output is always identified by the yellow **訓練** banner.


Phase 0（OBS透明出力スパイク）、Phase 1（ソリューション基盤）、
Phase 2（DTO・正規化・fixture）、Phase 3（ページ生成エンジン）、
Phase 4（優先度・ページ時計・割り込み制御）、
Phase 5（P2P受信・再接続・REST補完）、Phase 6（WPF操作画面・プレビュー）、
Phase 7（OBS Local View）、Phase 8（永続化・音・診断）が完了しています。
安全のため受信は自動接続せず、操作画面の「接続」から開始します。

## 開発環境から起動

```powershell
dotnet run --project src/EEWTelop.Wpf/EEWTelop.Wpf.csproj -c Release
```

旧版との互換性を保つため、設定・状態・ログは引き続き
`%LOCALAPPDATA%\QTelopper\2.x-beta`へ保存します。
保存先を変更する場合は`QTELOPPER_V2_BETA_DATA_DIRECTORY`を使用します。
外部サービスへの接続は自動開始せず、操作画面の「接続」から開始します。

## 復元・ビルド・テスト

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
```

配布物とソースアーカイブの作成方法は[`SOURCE_BUILD.md`](SOURCE_BUILD.md)を参照してください。

検証スクリプトは、存在する場合はプロジェクトローカルの.NET 8 SDK
（`.dotnet8`）を優先し、それ以外は`global.json`に適合するシステムSDKを使います。
