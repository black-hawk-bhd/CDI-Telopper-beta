# CDI External API v1（beta.46）

CDI-Telopper が正規化した本番受信情報を、同じPCの外部ツールへ提供する読み取り専用APIです。外部ツールからの字幕操作、設定変更、受信電文の投入はできません。内蔵の試験地図が無効な配布構成でも利用できます。

## 外部API連携機能の利用条件・免責事項

外部API連携機能および本機能から取得したデータには、次の条件が適用されます。

- 商用目的で利用しないでください。
- 取得したデータをインターネットへ送信しないでください。ただし、利用者が手動で行う動画投稿等は除きます。
- 取得したデータの一部または全部を、リレー・中継等によって複製・再配信しないでください。
- 連携先ソフトウェアを動画制作・公開・配信（YouTube等の生放送を含む）に使用する場合は、CDI-Telopperを利用していることを明記してください。この表記は、商用利用やデータの送信・再配信を許可するものではありません。
- 公共施設・商業施設等での運用、工作機器・医療機器等の制御、その他人命・身体の安全に関わるシステムには使用しないでください。
- 受信元API・データ提供元の利用規約、契約条件、二次利用・再配信条件を遵守してください。本条件は、提供元の制限を緩和したり、禁止された利用を許可したりするものではありません。

本機能は現状有姿で提供し、動作、連携先との互換性、データの正確性・完全性・即時性・継続性、特定目的への適合性について一切保証しません。本機能の利用または利用不能により生じた損害について、開発者は法令上認められる範囲で責任を負いません。利用者の責任で動作確認を行い、必ず公式の防災情報と併せて利用してください。

## 有効化と認証

「表示・出力」→「OBS Local View」を有効にし、「保存して反映」した後、「CDI External API v1」のチェックをONにしてください。APIのチェックは即時反映で、今回の起動中のみ有効です。初期OFFです。

「外部API 接続URLをコピー」で、トークン付きの status URLを取得できます。
例: `http://127.0.0.1:64270/api/v1/status?token=TOKEN`

- ポートはOBS Local Viewと共用。0（自動）なら再起動時に変わる場合があります。固定ポートはOBS Local View設定で指定します。
- ローカルIPv4（127.0.0.1）のみ待ち受けます。LAN公開やポート転送はしないでください。
- HTTPは `Authorization: Bearer TOKEN` を推奨。WebSocketではクエリ `?token=TOKEN` も使用できます。
- 専用のランダムな接続キーを使用し、OBS用トークンは受け付けません。OFF→ONまたはアプリ再起動で接続キーが変わります。無効化した既存WebSocketも終了します。
- 接続URL・キーは秘密情報です。公開、ログ出力、スクリーンショットへの写り込みを避けてください。受信元サービスのAPIキー等はレスポンスに含めません。
- CORSは許可しません。別ポートの地図ページからHTTPを直接fetchするのではなく、地図ツールのローカルサーバーが代理取得してください。WebSocketのOriginはHTTPのループバックのみ（またはOriginなし）。`file://` のnull Originや外部サイトは拒否します。
- OBS Local Viewを停止するとAPIも停止します。API接続数はOBSの音声再生先の接続数に加算しません。

## エンドポイント

| 接続 | 用途 |
| --- | --- |
| `GET /api/v1/status` | APIバージョン、アプリバージョン、起動セッションID、受信接続状態、受信元別状態、最終受信時刻 |
| `GET /api/v1/tsunami` | `sessionId`、`status`、`tsunami`（警報・予報と観測情報を分離した最新受信スナップショット） |
| `GET /api/v1/earthquake` | `sessionId`、`status`、`earthquake`（最新の地震情報） |
| `GET /api/v1/eew` | `sessionId`、`status`、`eew`（複数イベントのEEW状態） |
| `WS /api/v1/events` | 初回 `snapshot`、変更時 `update`、15秒程度ごとの `heartbeat`。status・tsunami・earthquake・eewの全スナップショット |

`status.capabilities` は `tsunami`, `earthquake`, `eew`, `events.websocket`。新しい任意項目はv1内で追加し、破壊的変更は別バージョンとする。クライアントは未知のフィールドを無視し、未対応の列挙値は「不明」として扱う。CDIの内部クラスのJSON直列化ではなく、外部向けに定義した値を返す。色・描画位置・特定プラグインの設定を含めない。

### 地震・EEWの共通データ

- `earthquake`: `apiVersion`, `revision`, `hasInformation`, `earthquake`（未受信はnull）。内側の電文は `eventId`, `provider`, `issuedAt`, `receivedAt`, `serial`, `isCancelled`, `isExpired`, `informationType`, `sourceMode`, `earthquake`, `points`, `headline`, `comment`。
- 電文中の `earthquake`: `originTime`, `maximumIntensity`, `hypocenter`（`name`, `latitude`, `longitude`, `depthKilometers`, `magnitude`）、`domesticTsunami`, `foreignTsunami`。
- `points`: `name`, `prefecture`, `isArea`, `intensity`, `stationCode`, `municipalityCode`, `municipalityName`, `seismicAreaCode`, `seismicAreaName`。コード未取得は空文字。地域代表情報を観測点の位置だと解釈しない。
- 震度は `0`～`4`, `5-`, `5+`, `6-`, `6+`, `7`, `5-?` の文字列。`5-?`は5弱以上と考えられるが詳細不明。不明はnull。深さはkm、マグニチュードは数値、不明はnull。震源位置の座標系は受信データの緯度・経度。
- 津波判定は正規化した列挙名（例 `Unknown`, `None`, `Checking`, `Watch`, `Warning`）。未取得の `Unknown` を「津波なし」として描画しない。
- `eew`: `apiVersion`, `revision`, `hasInformation`, `events[]`。電文に上記識別・時刻・震源のほか `isWarning`, `isFinal`, `isCancelled`, `expiresAt`, `isExpired`, `areas[]` を含む。各areaは `name`, `prefecture`, `intensityFrom`, `intensityTo`, `arrivalTime`。
- EEWの `expiresAt` は発表時刻から10分のAPI上の保持目安であり公式解除ではない。受信元が明示した失効も `isExpired` に反映するため、この時刻より前にtrueになる場合がある。`isExpired=true` または `isCancelled=true` を発表中として表示しない。最大100イベントを保持。地震は最新電文1件であり現在進行中とは限らない。別EventIDの地震取消で現在保持する地震を置き換えない。
- `hasInformation=false` は未取得であり「地震なし・EEWなし」を保証しない。接続中でも上流取得範囲の完全性は保証されない。地震・EEWの過去状態を勝手に推定・復元しない。

観測点の `latitude`, `longitude` は受信元が座標を提供した場合のみ数値、それ以外はnull。地域を勝手な位置へ変換しない。発生時刻が取得できない場合の `originTime` もnull（発表時刻とは別）。

WS URLは上記コピーURLの `http` を `ws` に、パスを `/api/v1/events` に変更します。認証クエリは保持します。通知反映は約1秒以内、同時WS接続は最大16です。遅い送信先は5秒で切断します。クライアントからアプリケーションメッセージを送ると切断します。

`sequence` はWS接続内の連番、`sessionId` はアプリ起動ごとのIDです。切断中の通知履歴の再送はしません。再接続時の初回スナップショットで全面更新してください。接続失敗時は1秒・2秒・4秒…最大30秒程度で再試行し、切断中や30秒以上通知がない場合は「情報更新停止」を表示してください。通知は最新状態方式で、短時間の中間更新は省略される場合があります。

認証失敗403、無効中404、存在しないパス404、GET以外405、WSハンドシェイク不正400、WS上限超過503です。キャッシュは禁止（no-store）です。

## 津波データの契約

`tsunami` の構造:

```json
{
  "apiVersion": "1",
  "revision": 0,
  "hasForecast": false,
  "forecast": null,
  "observation": null
}
```

これは取得前の状態です。**未受信であり、「津波なし」「警報解除」を意味しません。** 過去のローカル保存情報は復元しません。APIをONにする前でも、同じ起動セッションで受信した本番情報は保持します。

### 後から起動した場合の初期取得

外部APIをONにしたとき（またはONのままOBS Local Viewを再起動したとき）、[気象庁の現在の津波JSON](https://www.data.jma.go.jp/multi/data/VTSE41/jp.json)を1回取得します。CDI起動時点ではAPIはOFFなので、この通信も行いません。

- 字幕・音声・受信履歴・重複判定・OBS表示には流さず、外部APIの `forecast` だけ更新します。内蔵試験地図への入力追加ではありません。
- 成功後は通常の本番受信で更新します。**津波の受信元を有効にしてください。** このJSONを常時巡回する受信プロバイダーではありません。
- 失敗時は `failed` を通知し、60秒後に再試行します。OFFやサーバー停止で中断。OFF→ONで再取得できますがキーも変わります。
- HTTPエラー、不正JSON、必須項目欠落、未知コード、過大データを発表なしに変換しません。キャッシュ抑制を要求し、Ageが60秒を超える応答も拒否します。
- 正常な空 `item` / 解除項目のみを取得した場合に限り、初期取得の結果を発表なしとして扱います。発表日時が古くても「最後の解除発表」の場合があるため、取得日時と発表日時は別管理です。気象庁側の更新遅延まで保証するものではありません。
- 取得中に新着の本番津波予報が採用された場合、その新着を保持します。すでにある情報と同時刻または古い初期取得結果も上書きしません。

`tsunami` に以下を追加しています。

| 項目 | 内容 |
| --- | --- |
| `initialization.state` | `notStarted` / `loading` / `ready` / `failed` / `cancelled` |
| `initialization.startedAt`, `completedAt` | 取得の開始・完了日時 |
| `initialization.applied` | 初期取得結果を採用したか。新着優先ならfalse |
| `initialization.error` | 取得失敗の通知。内部例外や秘密情報は含めません |
| `forecastState` | `unknown` / `active` / `inactive` / `telegramCancelled` / `expired` |
| `forecastOrigin` | `startupSnapshot` / `live` / null |

`GET /status` の `tsunamiInitialization` にも取得状態を含め、WebSocketでも取得状態の変化を通知します。`ready`だけでは発表中かどうかは分かりません。`forecastState`と合わせて判断してください。取得失敗でも本番受信済みのforecastは消去しません。

- `forecast`: 最後に採用された警報・予報電文（VTSE41、P2P等）。VTSE51/52では置き換えません。
- `observation`: 最後に採用されたVTSE51またはVTSE52。両電文の独立した履歴ではありません。VTSE51は沿岸観測・地点予報、VTSE52は沖合観測を格納します。保持する `forecast` とEventIDが異なる場合は、組み合わせを防ぐためレスポンスの `observation` をnullにします。このnullは観測情報の取消・解除を意味しません。forecast未取得の場合は観測情報だけを返すことがあります。
- 各電文に `eventId`, `provider`, `telegramType`, `issuedAt`, `receivedAt`, `expiresAt`, `observationAsOf`, `isCancelled`, `isTelegramCancellation`, `isExpired`, `sourceMode`, `areas`, `item` を持ちます。
- 日時はオフセット付きISO 8601。値不明はnull。`areas`の`role`と`grade`は列挙名文字列です。高さは数値と原文を保持し、「巨大」「高い」等を無理に数値化しません。
- `sourceMode` はproductionのみ。ManualTest / Sandbox / HistoryRehearsalや手動再掲はAPIを更新しません。本番の表示フィルターや字幕消去に関係なく、採用された受信情報を保持します。
- 発表時刻が既存より古い同区分電文では上書きしません。重複・無効・無視された電文でも更新しません。
- `isCancelled` は既存正規化の解除・取消フラグ。`isTelegramCancellation=true` は電文取消であり、警報解除と断定してはいけません。
- `isExpired=true` は電文に明示された有効期限を過ぎた状態、または受信元が明示した失効状態です。後者では `expiresAt` がnullの場合もあります。期限がない電文の鮮度を自動保証するものではありません。失効は警報解除・電文取消と別の状態です。
- `item`は地図向け補助配列。予報区の `area.name` と `kind.code/name` を持ち、大津波警報52・津波警報51・津波注意報62のみ収録。観測電文、解除・取消・期限切れでは空です。**空配列だけで解除を判断しないでください。** 原資料の全項目は `areas` を確認します。

これは「最新の受信結果」であり、気象庁における現在の発表状況を保証するAPIではありません。受信停止中の値は最後の値として残ります。statusの接続状態、津波受信元の設定、電文時刻、期限を併せて扱ってください。P2Pの対象外メッセージ等でも全体の最終受信時刻は更新されるので、それだけでは津波情報の鮮度を判断できません。契約プロバイダー由来の情報は、その利用条件・再配信条件も確認してください。

## Bridge API 1.1（fixed-r8仕様）との関係

これは**CDIが外部へ公開するAPI v1**の仕様書です。Bridge API 1.1をそのまま転送するAPIではありません。Bridge受信にCDI External APIの有効化は不要です。BridgeのSSE `/api/v1/events` とCDIのWebSocket `/api/v1/events` は、接続先・形式とも別です。

- Bridge入力では新旧のイベント識別子、入れ子の震源、津波メタデータ＋JMA互換本文、取消専用イベントを変換します。外部公開データは本書の共通形式を維持します。
- r8初期情報は `mode=live` と確認できる場合のみ本番採用します。訓練・テスト・区分不明を本番情報へ変換しません。初期・再接続時の同期は字幕・音声を再実行しません。
- 同一EventIDの古い報を抑止し、津波予報・観測は別々の報番号で比較します。観測情報の取消で予報を解除しません。別EventIDへの電文取消で現在保持する予報・観測を置き換えません。
- Bridgeの `upstreamHealthy`、`upstreamState`、feedの `configured`・`stale` をCDIの接続状態に反映します。CDIのstatusには `Stale` 等の接続状態として現れます。これらは災害情報の解除フラグではありません。
- `available:false` や空本文だけでは解除と判断しません。本文のない明示的な取消はメタデータから判定します。期限切れ情報は状態として保持しますが、新着字幕・音声を出しません。
- Bridgeの `highestSerial`、`available`、`reason`、SSEの `sequence` はCDI公開APIの同名フィールドとして転送しません。クライアントは本書の状態フィールドを使ってください。

津波本文は `Head` / `Body.Tsunami` のJMA互換形式を前提とします。添付仕様の `payload: {}` だけから地域・高さを推測しません。対応範囲とr6向け互換パッチは[連携手順](../integrations/obs-earthquake/README.md)を参照してください。r6向けパッチをr8へ適用する必要はありません。

検証は仕様に基づく自動テストで実施済みです。**r8実機との接続確認は未実施**です。

## 指定の津波マップツールとの接続

確認したZIPの `server-src/main.go` は、`/api/jma/tsunami` から気象庁JSONを代理取得します。`index.html` はそのJSONの `item[].area.name` と `item[].kind.code` を使って海岸線を描画します。

CDI側だけの変更では、この既存ツールの接続先は変わりません。地図側サーバーに次のアダプターが必要です（今回ZIP内のEXE・ソースは変更していません）。

1. CDI接続URLと専用キーを地図側サーバーに設定する。ブラウザーへキーを渡さない。
2. サーバーから `GET /api/v1/tsunami` を取得する（またはWSの最新スナップショットを保持）。
3. `tsunami.forecast` がnull、`forecastState=unknown`、電文取消、期限切れの場合、正常な空itemへ変換せず、取得中・取得失敗・不明を地図側へ渡す。受信停止は更新停止として明示する（初期取得結果は取得時点の参考情報として区別可能）。既存地図のエラー表示を使う場合はHTTP503等を返す。
4. 正常な予報は `forecast.item` を既存形式のトップレベル `item` として返す。解除は `isCancelled && !isTelegramCancellation` を確認し、空itemへ反映する。
5. `reportDateTime` は `forecast.issuedAt` から生成する。海岸線は既存データを継続利用する。地名は完全一致で照合し、未対応地域を通知する。

このAPIには現状、津波電文に付随する震源・Mの項目や、海岸線形状は含めません。外部ツールの接続ごとには気象庁へアクセスせず、CDI側で初期取得した状態を共用します。

## 実装位置

- `src/EEWTelop.Wpf/Obs/ObsLocalViewServer.ExternalApi.cs`: HTTP/WS・認証・接続管理
- `src/EEWTelop.Wpf/Obs/ExternalApiState.cs`: 本番受信の投影とDTO
- `src/EEWTelop.Wpf/Obs/ExternalEarthquakeState.cs`: 地震・EEWの公開状態
- `src/EEWTelop.Infrastructure/Bridge/`: Bridgeの受信・新旧形式変換
- `tests/EEWTelop.Wpf.Tests/ExternalApiTests.cs`: API・状態分離テスト
- `tests/EEWTelop.Wpf.Tests/BridgeIntegrationTests.cs`: Bridge互換・取消・失効・初期同期テスト
