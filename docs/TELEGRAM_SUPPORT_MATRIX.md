# 対応電文と処理経路

この表は「受信できる可能性がある形式」と「CDI-Telopperが字幕化する形式」を区別するための開発者向け資料です。契約、チャンネル、表示フィルター、電文内容によっては、対応形式でも表示されません。

## P2P地震情報API

| コード | 内容 | 正規化先 | 字幕生成 |
| --- | --- | --- | --- |
| 551 | 地震情報 | `QuakeEvent` | `QuakePageComposer` |
| 552 | 津波情報 | `TsunamiEvent` | `TsunamiPageComposer` |
| 556 | EEW | `EewEvent` | `EewPageComposer` |

P2Pの入口は `P2pEventNormalizer` です。切断後のREST復旧対象とライブ受信対象は同一ではありません。復旧処理を変更する場合は、`P2pEventSource`、`P2pRestRecoveryClient`、`P2pHistoryMessageSource`を併せて確認します。

## 気象庁防災情報XML系

DMDATA.JP、NII履歴、ローカルXML、テストライブラリ、AXISの `jmx-*` 復元結果は、共通の `JmaXmlEventNormalizer` を通ります。

| 電文 | 内容 | 正規化先 | 補足 |
| --- | --- | --- | --- |
| VXSE43 | 緊急地震速報（警報） | `EewEvent` | 警報と取消を対象 |
| VXSE45 | 緊急地震速報（地震動予報） | `EewEvent` | 警報を含む電文または取消のみ字幕対象 |
| VXSE51 | 震度速報 | `QuakeEvent` | 津波注意文も正規化対象 |
| VXSE52 | 震源に関する情報 | `QuakeEvent` | M不明、巨大地震表現を含む |
| VXSE53 | 震源・震度に関する情報 | `QuakeEvent` | 観測点・地域震度を含む |
| VXSE62 | 長周期地震動に関する観測情報 | `QuakeEvent` | 長周期階級を保持 |
| VTSE41 | 津波警報・注意報・予報 | `TsunamiEvent` | 継続状態と解除を扱う |
| VTSE51 | 津波情報 | `TsunamiEvent` | 到達予想・観測を扱う |
| VTSE52 | 沖合の津波観測情報 | `TsunamiEvent` | 情報役割を区別する |
| VYSE50 | 南海トラフ地震臨時情報 | `QuakeEvent` | 専用のIssueTypeで区別 |
| VYSE60 | 北海道・三陸沖後発地震注意情報 | `QuakeEvent` | 専用のIssueTypeで区別 |
| VFVO50 | 噴火警報・予報 | `VolcanoEvent` | 対象地域と防災文を保持 |
| VFVO56 | 噴火速報 | `VolcanoEvent` | 速報種別として区別 |
| VPWW53/54 | 旧体系の気象警報・注意報 | `WeatherWarningEvent` | 移行検証用。AXIS旧形式は後段で抑止 |
| VPWW55～61 | 2026年体系の気象警報・注意報 | `WeatherWarningEvent` | 警報種別ごとの電文 |
| VPWS50 | 気象警報・注意報まとめ | `WeatherWarningEvent` | 正規化可能だが通常購読では重複回避 |
| VPOA50 | 記録的短時間大雨情報 | `WeatherWarningEvent` | 専用InformationType |
| VPBS50 | 気象防災速報 | `WeatherWarningEvent` | 専用InformationType |
| VPBS51 | 気象防災速報（潮位） | `WeatherWarningEvent` | 専用InformationType |
| VPHW50 | 竜巻注意情報 | `WeatherWarningEvent` | 専用InformationType |
| VPHW51 | 竜巻注意情報・目撃情報付き | `WeatherWarningEvent` | 専用InformationType |

対応外の `jmx-meteorology` 電文は、壊れた入力ではなく表示対象外として静かに無視します。対応電文を追加する場合は、検出、正規化、受信元選択、字幕生成、テストのすべてを更新します。

遠地の大規模噴火情報もVXSE53として届く場合があります。`QuakePageComposer`は、M不明・最大震度不明・観測点なしで、自由付加文に「大規模な噴火が発生しました」または「大規模噴火が発生しました」がある場合、地震概要、定型の津波案内、震度情報なしの3ページを省略します。自由付加文の噴火情報・潮位観測・津波到達予想は全文をページ分割して表示します。取消と訂正の表示は維持し、M不明だけでは省略しません。AXIS、DMDATA.JP、履歴・ローカルXMLで共通の表示処理です。

## AXIS

| チャンネル | 入力 | 処理 |
| --- | --- | --- |
| `eew` | AXIS専用JSON | `AxisEventNormalizer`が直接 `EewEvent`へ変換 |
| `jmx-seismology` | JMA JSON表現 | XMLへ復元後、`JmaXmlEventNormalizer`へ渡す |
| `jmx-meteorology` | JMA JSON表現 | XMLへ復元後、`JmaXmlEventNormalizer`へ渡す |
| `jmx-volcanology` | JMA JSON表現 | XMLへ復元後、`JmaXmlEventNormalizer`へ渡す |

AXISの `eew` は気象庁XML形状ではありません。`jmx-*` と同じDecoderへ統合しないでください。警報条件、取消、トークン、チャンネル契約はそれぞれ独立して確認します。

## Wolfx

| WebSocket | 入力 | 処理 |
| --- | --- | --- |
| `wss://ws-api.wolfx.jp/jma_eew` | JMA EEW JSON | 警報・取消を `EewEvent`へ変換。予報のみは無視 |
| `wss://ws-api.wolfx.jp/jma_eqlist` | JMA地震情報一覧JSON | VXSE53相当の「震源・震度情報」。`No1`の最新項目を `QuakeEvent`へ変換 |

WolfxはEEW・地震情報にだけ割り当て可能です。1分間隔の `heartbeat` を受けた場合は同じ接続へ `ping` を返し、字幕イベントにはしません。両分類がWolfxの場合は2本のWebSocketを独立して再接続します。選択されていない分類のWolfx WebSocketには接続しません。

Wolfxの地震情報は現在VXSE53相当の「震源・震度情報」だけです。VXSE51、VXSE52、VXSE62などの個別電文をWolfxから取得できるものとして扱わないでください。

## 追加・変更時のチェックリスト

1. 入力形式と電文コードをProvider層で受理できるか。
2. 共通の `DisasterEvent`へ情報を欠落なく正規化できるか。
3. `ProviderSelectionEventNormalizer`が正しい分類として採用するか。
4. Composerが通常、続報、取消、解除を正しく表示するか。
5. `PriorityCoordinator`の更新・優先度・継続状態を壊さないか。
6. 本番受信、履歴、ローカルXML、テストライブラリで同じ結果になるか。
7. 対応表、README、プロバイダー資料を更新したか。
