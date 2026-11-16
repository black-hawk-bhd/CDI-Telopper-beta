# CDI-Telopper user guide

[日本語](USER_GUIDE.md) | English

This guide covers **2.0.0-beta.68** for Windows x64. Menu names appear as they do in the Japanese interface. [Download](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/tag/v2.0.0-beta.68) / [Release notes in Japanese](../RELEASE_NOTES_2.0.0-beta.68.md)

## Install or update

1. Download the folder or single-EXE ZIP and check its SHA-256 against `SHA256SUMS.txt`. Both packages are self-contained.
2. Before updating, exit the old version by right-clicking its tray icon and selecting **終了** (Exit). Closing the main window with X sends it to the system tray.
3. Extract the entire ZIP into a separate writable directory and run `CDI-Telopper.exe`. Regular updates reuse your existing settings. The default data directory is `%LOCALAPPDATA%\QTelopper\2.x-beta`.

Hover over the CDI-Telopper icon in the notification area to check the current reception status.

## Set up reception and captions

1. Under **受信** (Reception) → **受信元** (Sources), select a provider per category. Select **受信しない** (Do not receive) for categories you do not use.
2. For DMDATA.JP, select your subscription category and use an API key or authorize in your browser with OAuth. **契約情報を更新** (Refresh contract information) requires `contract.list`. AXIS requires a valid access token and permissions for the selected channels. [DMDATA.JP setup](dmdata-oauth.md)
3. Use **フィルター** (Filters) to choose categories, areas and intensities. Use **表示** (Display) for layout and pagination and **音声** (Audio) to select sounds and when they play.
4. Select **保存して反映** (Save and apply), check the output under **テスト** (Test) and in the silent browser preview, then select **接続** (Connect).

Enable **起動時に受信APIへ自動接続** (Connect to reception APIs on startup) under **受信** (Reception) → **受信元** (Sources) and save. The app connects to your saved providers the next time it starts. This setting is off by default.

With multiple sources, the window header shows the connection state of each source. The **接続** (Connection) panel and system tray also show how many times each source has entered the reconnect wait state since the app started. If the tray tooltip is too long, right-click the icon and open **API別の状態と再接続回数** (Status and reconnect count by API).

JMA XML polls at intervals of at least 60 seconds and carries no EEW in this app. If automatic fallback is enabled and an eligible DMDATA.JP, P2PQuake or AXIS connection is faulted or reconnecting for at least 30 seconds, CDI-Telopper can temporarily use JMA XML for supported non-EEW categories. It returns to the selected provider after reconnection. YDITS supports public EEW warnings (VXSE43). **YDITS（テストモード）** (Test mode) displays messages in the browser preview review list without automatically sending them to OBS. YDITS warning-area display and cancellations without enough information to match a warning are unsupported. [Data sources](data-sources.md) / [YDITS VXSE43 documentation](https://www.ydits.net/api/vxse43/)

## Connect OBS and preview

- Enable **OBS Local View** in **出力** (Output) and save. For manual setup, add the four OBS URLs to separate OBS browser sources for earthquake, EEW, tsunami and weather. Enable **Control audio via OBS** on each source. The default canvas is 1920×1080. [OBS source names and setup](../README_EN.md#obs-output)
- For automatic registration, enable the OBS WebSocket server and enter its URL and password under **OBS WebSocket 自動登録**. Turn on **OBSのブラウザーソースを自動作成・URL更新** (Create OBS browser sources and update URLs automatically) and save. Select **今すぐOBSと同期** (Sync with OBS now) when needed.
- Select **ブラウザープレビューを開く（無音）** (Open silent browser preview) to inspect active captions and individual received messages. Check notification audio in OBS. If you registered OBS URLs manually, copy the current URLs after restarting CDI-Telopper.

## Review past messages and run rehearsals

Open **受信・過去電文を確認** (Review received and past messages). Under **取得元と再掲・訓練の表示設定**, select a history source such as the official JMA XML long feeds, the NII archive, a local JMA XML file or P2PQuake history. Use **過去電文を取得** (Fetch past messages) to populate the list. Fetching alone sends no captions or audio. Both past-information presentation and live-message replay are available during live reception. The live-message replay option is reserved for messages received in production. [History retrieval limits](data-sources.md#manual-history--replay)

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
