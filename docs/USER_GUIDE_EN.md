# CDI-Telopper user guide

[日本語](USER_GUIDE.md) | English

This guide covers **2.0.0-beta.75** for Windows x64. Menu names appear as they do in the Japanese interface. [Download](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/tag/v2.0.0-beta.75) / [Release notes in Japanese](../RELEASE_NOTES_2.0.0-beta.75.md)

## Install or update

1. Download the self-contained single-EXE ZIP and verify its SHA-256 from the release page. The lightweight package requires the x64 .NET 10 Desktop Runtime at version 10.0.14 or a later .NET 10 patch. [Microsoft downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
2. Before updating, exit the old version by right-clicking its tray icon and selecting **終了** (Exit). Closing the main window with X sends it to the system tray.
3. Extract the entire ZIP into a separate writable directory and run `CDI-Telopper.exe`. Regular updates reuse your existing settings. The default data directory is `%LOCALAPPDATA%\QTelopper\2.x-beta`.

Under **Version information → Data directory**, select `Settings` beside `CDI-Telopper.exe` to store data with the application. Apply the location, exit from the system tray, and start the application again. CDI-Telopper copies the currently saved settings if the destination has no settings file. If it already has settings, CDI-Telopper loads those. When raw message archiving is enabled, new XML and JSON messages go into `raw-reception` under the selected location. You can still find earlier messages in their original folder.

Hover over the CDI-Telopper icon in the notification area to check the current reception status.

## Set up reception and captions

1. Under **受信** (Reception) → **受信元** (Sources), select a provider per category. Select **受信しない** (Do not receive) for categories you do not use.
2. For DMDATA.JP, select your subscription category and use an API key or authorize in your browser with OAuth. **契約情報を更新** (Refresh contract information) requires `contract.list`. AXIS requires a valid access token and permissions for the selected channels. [DMDATA.JP setup](dmdata-oauth.md)
3. Use **フィルター** (Filters) to choose categories, areas and intensities. Use **表示** (Display) for layout and pagination and **音声** (Audio) to select sounds and when they play.
4. Select **保存して反映** (Save and apply), check the output under **テスト** (Test) and in the silent browser preview, then select **接続** (Connect).

For the Hokkaido and Sanriku subsequent earthquake advisory (VYSE60), select JMA XML or Wolfx in the shared row labeled **南海トラフ地震の情報** and **北海道・三陸沖後発地震注意情報**. VYSE60 from DMDATA.JP or AXIS follows the **地震情報** (Earthquake information) setting.

Enable **起動時に受信APIへ自動接続** (Connect to reception APIs on startup) under **受信** (Reception) → **受信元** (Sources) and save. The app connects to your saved providers the next time it starts. This setting is off by default.

With multiple sources, the window header shows the connection state of each source. The **接続** (Connection) panel and system tray also show how many times each source has entered the reconnect wait state since the app started. If the tray tooltip is too long, right-click the icon and open **API別の状態と再接続回数** (Status and reconnect count by API).

JMA XML polls at intervals of at least 60 seconds and carries no EEW in this app. If automatic fallback is enabled and an eligible DMDATA.JP, P2PQuake or AXIS connection is faulted or reconnecting for at least 30 seconds, CDI-Telopper can temporarily use JMA XML for supported non-EEW categories. It returns to the selected provider after reconnection. YDITS production and test reception are temporarily suspended. A previous YDITS selection is treated as **受信しない** (Do not receive) for EEW. [Data sources](data-sources.md)

Under **フィルター** (Filters) → **表示フィルター** (Display filters), enable **気象庁以外の震度観測点を非表示** (Hide non-JMA intensity stations) to omit stations whose names end in a full-width `＊` from detailed intensity captions received from Wolfx, JMA XML, DMDATA.JP or AXIS JMA telegrams (`jmx-seismology`). The option is off by default. P2PQuake stations are outside the scope of this option. The summary uses the reported maximum intensity. The telegram review window lists all received stations under **震度観測点（電文）**. [JMA station notation](https://www.data.jma.go.jp/suishin/oshirase/pdf/20250703.pdf)

Under **気象情報** (Weather information) in **フィルター** (Filters), **警報** and **危険警報** can be toggled separately. On upgrade, both inherit the previous **警報** setting.

In **表示** (Display), **情報種別をバッジではなく字幕で表示** shows warning types and intensities as subtitles in live output, training, tests, rehearsals, and replays. If this option is off, the separate message-review setting still controls training, rehearsal, and replay output.

Under **表示** (Display) → **ページと文字** (Pages and text), choose an installed Windows font from **字幕フォント** (Subtitle font). Saving applies the font to both the browser preview and OBS. The default is BIZ UDPGothic, which also supplies missing characters when available.

**ページ秒数** (Seconds per page) sets how long each page is shown. Earthquake reports and weather warnings or advisories clear automatically after all pages have been shown. Three pages at four seconds each take 12 seconds. Set the separate EEW timer under **緊急地震速報の自動消去秒** (EEW auto-clear time).

Expand **電文別のページ秒数** (Seconds per page by bulletin type) to set a separate duration for bulletins such as seismic intensity alerts (VXSE51) and detailed earthquake reports (VXSE53). Enter 0 to use the shared page duration. Save the settings to apply the durations to OBS, tests, and replays as well.

Under **出力** (Output) → **字幕と一緒に送出する画像** (Image shown with captions), register one PNG per information category. Set the left position (X), top position (Y), and width in pixels on the 1920×1080 canvas. After saving, the image appears in OBS and the browser preview only while the matching caption is visible. Transparency and aspect ratio are preserved. PNGs may be up to 2 MB and 4096 pixels per side.

In **表示** (Display), **緊急地震速報の継続報で更新された地域名をオレンジ色にする** highlights areas already in the previous warning when their expected intensity, arrival time or warning status changes. It applies to horizontal and vertical OBS layouts and the in-app preview. The option is off by default.

In **表示** (Display), **緊急地震速報に予想最大震度・推定マグニチュードを表示** adds available forecast maximum intensity and estimated magnitude to the warning in the preview and OBS. Both values are labeled as estimates. The option is off by default. A cancellation replaces the matching warning and removes its estimates.

In **表示** (Display), **津波注意報以上の発表中は震度速報の定型注意文を省く** omits the provisional tsunami caution sentence from a seismic intensity bulletin when tsunami captions are enabled and an active advisory or warning has already been received for the same earthquake. If the earthquake IDs cannot be matched, the caution still appears. The option is off by default.

**津波注意報以上の発表中は震度速報を表示しない** hides the entire seismic intensity bulletin for the same earthquake while a tsunami advisory or warning is active and tsunami captions are enabled. An advisory or warning that arrives later also removes a bulletin that is already visible or queued. The received bulletin stays in the message review list. If both options are enabled, hiding the bulletin takes precedence. A forecast alone or an unavailable earthquake ID leaves the bulletin visible. The option is off by default.

Under **フィルター** (Filters) → **表示フィルター** (Display filters) → **地震情報** (Earthquake information), you can toggle **南海トラフ地震の情報** (Nankai Trough earthquake information) and **北海道・三陸沖後発地震注意情報** (Hokkaido and Sanriku subsequent earthquake advisory) separately. Both are on by default. Saving an unchecked option clears its captions. Received reports remain available in message review. To show routine Nankai Trough commentary, enable both the Nankai Trough option and **南海トラフの定例解説をテロップに表示**. The routine commentary option is off by default.

## Connect OBS and preview

- Enable **OBS Local View** in **出力** (Output) and save. For manual setup, add the four OBS URLs to separate OBS browser sources for earthquake, EEW, tsunami and weather. Enable **Control audio via OBS** on each source. The default canvas is 1920×1080. [OBS source names and setup](../README_EN.md#obs-output)
- For automatic registration, enable the OBS WebSocket server and enter its URL and password under **OBS WebSocket 自動登録**. Turn on **OBSのブラウザーソースを自動作成・URL更新** (Create OBS browser sources and update URLs automatically) and save. Select **今すぐOBSと同期** (Sync with OBS now) when needed.
- Select **ブラウザープレビューを開く（無音）** (Open silent browser preview) to inspect active captions and individual received messages. Check notification audio in OBS. If you registered OBS URLs manually, copy the current URLs after restarting CDI-Telopper.
- Under **受信済み電文の個別確認** (Review individual messages), use **南海トラフの定例解説を表示** (Show routine Nankai Trough commentary) to control whether routine reports appear in the review list. The checkbox is on by default.

## Review past messages and run rehearsals

Open **受信・過去電文を確認** (Review received and past messages). Under **取得元と再掲の表示設定** (Sources and replay settings), select a history source such as the official JMA XML long feeds, the NII archive, a local JMA XML file or P2PQuake history. Use **過去電文を取得** (Fetch past messages) to populate the list. Fetching alone sends no captions or audio. Both past-information presentation and live-message replay are available during live reception. The live-message replay option is reserved for messages received in production. [History retrieval limits](data-sources.md#manual-history--replay)

In **テスト** (Test) → **生データ・テストライブラリー** (Test library), select **XMLフォルダーを一括登録** (Import XML folder). Each XML file, including files in subfolders, is registered as a training case. The import reports the number of cases and unreadable files. Filter the list by information category or bulletin type, such as VPBS50.

Select the `TrainingSamples` folder in the extracted ZIP to import the 185 included JMA sample files. [Attribution and terms](../jmaxml_20261118_CDI-Telopper/README.md)

Disconnect live reception before running built-in tests, history rehearsals, test-library cases, OBS audio previews or the Simulator connection. [Simulator connection in Japanese](disaster-simulator.md)

## Connect the External API

1. In **出力** (Output), enable **OBS Local View** and save. In **CDI External API v1**, turn on **外部APIを有効化** (Enable External API) and copy the connection URL with **外部API 接続URLをコピー**. Register it in the client on the same PC.
2. To reuse the URL across restarts, enter an unused fixed port, turn on **起動時に外部APIを有効化** (Enable External API on startup), and save. Select **接続キーを保存** (Save connection key) to store the key encrypted for your Windows user.
3. After changing the port or reissuing the key, register the new URL in your client. Use the separate rehearsal API and its own URL for training data. [API endpoints and schema in Japanese](external-api-v1.md) / [External API integration terms](../TERMS_EN.md#4-external-api-integration-terms)

## Troubleshoot and reset

- For reception problems, check the configured source, credentials and **ログ** (Log). If a received message produces no caption, review its display result and the active filters.
- In **受信・過去電文確認** (Received and past telegrams), select an earthquake and expand **震度観測点（電文）** to see the reported station names and intensities. Review pages show furigana for seismic regions and tsunami forecast areas when a reading can be identified.
- For External API connection problems, check that OBS Local View and the External API are running and that the port and key match the client. If the port is in use by another application, select a free port.
- **バージョン情報** (About) → **設定の初期化** (Reset settings) restores the defaults after confirmation and clears the saved External API key. Use **認可を解除** (Revoke authorization) in Reception settings to revoke DMDATA.JP OAuth access.
- Include the app version and steps to reproduce when reporting an issue. Check diagnostic files for personal information, credentials and connection URLs before sharing. [GitHub Issues](https://github.com/black-hawk-bhd/CDI-Telopper-beta/issues)

[Terms and disclaimer](../TERMS_EN.md) / [Privacy Policy](../PRIVACY_EN.md)
