# CDI-Telopper

[English](README_EN.md) | 日本語

Comprehensive Disaster Information Telopper（CDI-Telopper）は、地震・津波・気象・火山・南海トラフに関する防災情報を受信し、OBS向け字幕として出力するWindowsアプリです。

現在の公開版は **2.0.0-beta.34** です。開発中のベータ版であるため、本番配信へ導入する前に、利用環境で受信、再接続、OBS出力、音声、取消・解除を十分に確認してください。本ソフトウェアだけを防災判断の根拠にせず、必ず気象庁などの公式情報も確認してください。

- [2.0.0-beta.34をダウンロード](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/tag/v2.0.0-beta.34)
- [詳細README・操作説明・仕様書](README_CDI-Telopper_2.0.0-beta.34.txt)
- [ソースからのビルド方法](SOURCE_BUILD.md)
- [開発者向けコードガイド](docs/DEVELOPER_GUIDE.md)

## 主な機能

- EEW、地震、津波、気象、火山、南海トラフ情報の受信と字幕生成
- 情報分類ごとにP2P地震情報、DMDATA.JP、AXIS、Wolfx、または「受信しない」を選択
- 選択された情報に不要なプロバイダーAPIへは接続しない構成
- 同一イベントの続報、取消、解除、重複、古い更新報を考慮した表示制御
- 注意報・警報解除時に対象地域と解除種別を表示
- 本番受信電文と過去電文を確認できる独立ウインドウ
- 過去電文を使った訓練再表示とテストシナリオ
- OBS Local ViewとOBS WebSocket 5.xによるブラウザーソース自動登録・URL更新
- OBSへ集約した通知音声出力
- AXISトークンの期限確認と、期限前の自動更新
- ログ、生データ保存、診断ZIP、設定バックアップ

## 対応する主な情報

| 分類 | 主な対応内容 |
| --- | --- |
| EEW | P2P EEW、VXSE43、警報を含むVXSE45、AXIS `eew`、Wolfx JMA EEW |
| 地震 | VXSE51、VXSE52、VXSE53、VXSE62、VYSE60、P2P地震情報、Wolfx JMA地震情報 |
| 津波 | VTSE41、VTSE51、VTSE52、P2P津波情報 |
| 気象 | VPWW55～61、VPOA50、VPBS50・51、VPHW50・51 |
| 火山 | VFVO50、VFVO56 |
| 南海トラフ | VYSE50 |

配信元から届くすべての電文を字幕化するわけではありません。選択外の受信元、対応外電文、警報条件を満たさないEEW、表示フィルターで除外された情報、破損・重複・古い電文などは表示されない場合があります。

## 受信プロバイダー

### P2P地震情報 API

主にEEW、地震情報、津波情報で利用します。ProductionとSandboxを選択できます。

https://www.p2pquake.net/develop/

### DMDATA.JP

利用者自身の契約とAPIキーが必要です。EEWは契約に合わせて警報契約（VXSE43）または予報契約（VXSE45）を選択します。予報契約では、警報を含むVXSE45と取消を字幕対象とします。

法人向けの方は法人向けプランの契約が必要です。

https://dmdata.jp/

### AXIS

試験的プロバイダーです。利用者自身が取得した有効なアクセストークンと、利用するチャンネルの契約が必要です。`eew`、`jmx-seismology`、`jmx-meteorology`、`jmx-volcanology`を、選択した情報分類に応じて使用します。

※非商用に限り無料で利用できます。商用利用はできません。

https://axis.prioris.jp/

### Wolfx

認証不要の公開WebSocket APIです。EEWと地震情報の受信元として選択できます。EEWでは警報・取消のみを字幕対象とし、予報のみの更新は表示しません。

**注意：Wolfxの地震情報は、現在はVXSE53相当の「震源・震度情報」のみです。** 震度速報（VXSE51）、震源に関する情報（VXSE52）、長周期地震動に関する観測情報（VXSE62）などはWolfxから取得できません。地震情報は配信された一覧の最新項目を取り込み、既存のイベントID重複判定を適用します。

Wolfxは非公式サービスです。本ソフトだけを防災判断の根拠にせず、気象庁などの公式情報を併用してください。利用規約、接続数、再配信条件は提供元で確認してください。

https://wolfx.jp/docs/open-api/

DMDATA.JPとAXISの認証情報はWindows DPAPI CurrentUserで暗号化して保存します。認証情報をソース、配布ZIP、ログ、診断ZIPへ平文で含めない設計です。

## OBS出力

OBSへは次の4つのブラウザーソースを登録します。各ソースは1920×1080を前提とします。

- CDI-Telopper 地震字幕・全ての音声
- CDI-Telopper 緊急地震速報
- CDI-Telopper 津波字幕
- CDI-Telopper 気象情報

音声ミキサーの対象は「CDI-Telopper 地震字幕・全ての音声」だけです。ほかの3ソースは音声を無効にします。OBS WebSocket自動同期を使うと、ソースの不足分作成、起動ごとに変わるURLの更新、旧名称からの移行を行えます。

地震・津波地図機能と、PC画面へ常時重ねるデスクトップオーバーレイは廃止済みです。PC上での確認にはプレビューと「受信・過去電文を確認」ウインドウを使用します。

## 動作環境

- Windows 10またはWindows 11（64ビット）
- OBS Studio 28以降を推奨
- 利用する受信プロバイダーへ接続できるインターネット環境
- AXISまたはDMDATA.JPを使う場合は、有効な契約と認証情報

GitHub Releasesの配布物は.NET 8自己完結型です。通常利用ではVisual Studioや.NET SDKをインストールする必要はありません。

## インストールと初回起動

1. [Releases](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases)からZIPをダウンロードします。
2. 必要に応じて`SHA256SUMS.txt`と照合します。
3. ZIP内から直接実行せず、書き込み可能な通常フォルダへ完全に展開します。
4. `CDI-Telopper.exe`を起動します。
5. 情報分類ごとの受信元、認証情報、OBS、音声、表示条件を設定します。
6. テスト表示を確認してから「接続」を押します。

多重起動は抑止されます。ウインドウ右上の「×」では完全終了せず、タスクトレイへ格納されます。完全終了する場合は、タスクトレイの右クリックメニューから「終了」を選択してください。

設定、状態、ログは互換性維持のため、既定では`%LOCALAPPDATA%\QTelopper\2.x-beta`へ保存します。保存先を変更する場合は`QTELOPPER_V2_BETA_DATA_DIRECTORY`を使用します。

## セキュリティ上の注意

- AXISトークン、DMDATA.JP APIキー、OBS WebSocketパスワードを公開しないでください。
- ウイルス対策ソフトの例外が必要な場合は、配布物専用フォルダだけに限定してください。
- プロジェクト、ドキュメント、ダウンロードをまとめた広い親フォルダ全体を例外にしないことを推奨します。
- 配布ZIPはReleaseページとSHA-256を確認してから展開してください。
- 外部サービスの契約条件、再配信条件、同時接続数は各提供元で確認してください。

## ソースからの検証

必要な環境はWindows 10/11 x64、.NET 8 SDK、PowerShellです。Visual Studioは必須ではありません。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

このスクリプトは依存関係を復元し、Release構成で全プロジェクトをビルドして、自動テストを実行します。現在のbeta.34では477件のテストを確認しています。

配布物を作成する場合は次を実行します。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.34
```

フォルダ版、単一EXE版、`version.json`、`SHA256SUMS.txt`が`artifacts\release\2.0.0-beta.34\win-x64`へ生成されます。詳しくは[SOURCE_BUILD.md](SOURCE_BUILD.md)を参照してください。

## 開発者向け資料

コードを変更する場合は、最初に[開発者ガイド](docs/DEVELOPER_GUIDE.md)を参照してください。アーキテクチャ、機能別の実装・テスト対応表、対応電文、設定保存の流れ、安全な変更手順、読解負荷が高い箇所の案内を `docs` にまとめています。

## ライセンスと出典

- 本体ライセンス：[LICENSE](LICENSE)
- データ出典と履歴取得：[docs/data-sources.md](docs/data-sources.md)
- 音声ライブラリと音源の扱い：[docs/assets-license.md](docs/assets-license.md)

利用者が指定する音声ファイルはリポジトリや配布物へ同梱しません。
