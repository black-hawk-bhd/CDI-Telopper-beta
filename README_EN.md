# CDI-Telopper

Beta.49 restores long-period ground-motion classes and areas from Simulator messages. The Simulator must supply `longPeriodIntensity`; the normal JMA XML reception path is unchanged.

Beta.45 unifies non-river weather headings: information type in the badge, area beside it, and status only when explicitly available. Continuation pages, preview, OBS and telegram review retain the heading.

Beta.43: weather captions show only the information type in the badge, with prefecture/status alongside it and municipalities below. Tsunami observation introductions and the active warning/advisory announcement now appear on separate pages.

Beta.48: the built-in map feature has been fully removed, including its buttons, windows, renderer, geographic assets, and build switch. CDI External API output and received hypocenter/intensity-point information remain available for external plugins.

English | [日本語](README.md)

Beta.48: [Disaster Simulator 0.3.1 integration](docs/disaster-simulator.md) is available from the Test tab. Earthquake, EEW and tsunami updates are always treated as training. Production reception must be disconnected first; simulator updates are not forwarded through CDI's production external API.

Beta.41 fixes JMA XML telegrams being rejected before normalization. “気象庁XML” is a manually selected, unauthenticated JMA XML pull provider for existing supported non-EEW categories. Select it in reception settings and save. Polling occurs at intervals of at least 60 seconds. Telegrams predating the initial connection are not displayed as new alerts. Publication may be delayed or interrupted; long outages may cause missed messages. There is no automatic AXIS failover. See [JMA's usage notes](https://xml.kishou.go.jp/xmlpull.html).

**Comprehensive Disaster Information Telopper (CDI-Telopper)** is a Windows application that receives disaster information related to earthquakes, tsunamis, weather, volcanoes, and the Nankai Trough, then generates captions for OBS.

beta.53 displays active weather warning and advisory area names in two rows per page by default; the third row moves to a new page with its prefecture and status repeated. A checkbox restores the prior three-row layout. Release pages and other information types are unchanged. The EXE/window/tray icon and the control-window brand logo have been updated.

beta.52 adds a silent browser preview monitor with four output panels and independent telegram review. Enable OBS Local View and use the browser-monitor button. Chrome is preferred, with the default browser as fallback. Review does not replay output or audio. Keep its authenticated URL private.

beta.51 adds per-domain upstream health, a separately authenticated rehearsal API, Simulator disconnect-state retention, and optional intensity-class-separated pages (enabled by default). See the external API specification.

beta.50 improves unreported-intensity normalization and labeling, removes trailing full-width station markers only from display text, and preserves observed and unreported entries at the same place. Simulator decoding and additive external API fields are improved while training/live separation remains enforced.

The current public release is **2.0.0-beta.57** (October 4, 2026). This is a development beta. Before using it in a live broadcast, thoroughly test reception, reconnection, OBS output, audio, cancellations, and the lifting of warnings and advisories in your own environment. Do not rely on this application as your sole source for safety decisions. Always confirm critical information through official sources such as the Japan Meteorological Agency (JMA).

- [Download 2.0.0-beta.57](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/tag/v2.0.0-beta.57)
- [Detailed Japanese beta.57 manual and specification](README_CDI-Telopper_2.0.0-beta.57.txt)
- [Build from source](SOURCE_BUILD.md)

## Main features

beta.57 replaces the WPF preview with the silent browser preview, showing earthquake, EEW, tsunami and weather panels together. Repeated automatic launches of the same URL are suppressed.

beta.57 adds individual exclusions for earthquake maximum intensities 1 through 6-upper, selectable prefecture filtering for events and/or points, and an option to include intensity 1–2 points in earthquakes with a maximum of 3 or higher (default off). Intensity 7, unknown/unreported intensity and cancellations are not excluded by intensity selection. Source data and external API values remain unchanged.

beta.57 adds per-category audio timing: legacy playback (default), first new announcement only, or new announcements and escalations. Classification uses the announcements observed during the session, with training and production kept separate; it cannot guarantee the true first announcement when starting mid-event or when metadata is missing.

beta.57 routes earthquake, EEW, tsunami and weather audio through their matching OBS browser sources, including audio tests. All four sources require OBS audio control. The 883 automated tests pass; actual reception and visual/audio checks with real OBS remain unperformed. See the [change notes](RELEASE_NOTES_2.0.0-beta.57.md) and [validation results](VALIDATION_2.0.0-beta.57.md).

beta.56 adds manual DMDATA.JP contract-information retrieval in reception settings. It displays each returned plan's contract status, daily price and monthly maximum, start time, and additional connection allowance; these prices are not an actual bill. Retrieval failures are not reported as uncontracted plans. Contract information is shown temporarily in the control window, not saved to files, settings, logs or diagnostic ZIPs, and not sent to OBS captions or the external API. There is no periodic polling or contract modification. API keys need `contract.list`; OAuth users must explicitly authorize that additional scope with “契約情報も認可”. It must also be permitted in the registered OAuth client. The embedded client's registration for this scope and retrieval with a real account have not been verified. See the [contract-information setup guide](docs/dmdata-oauth.md#契約情報の表示).

beta.56 separates settings into “受信” (Reception), “フィルター” (Filters), “表示” (Display), and “出力” (Output). Reception contains providers and authentication; Filters selects the information to display; Display controls pages, text and repetitions; Output contains canvas, OBS and external API settings. Setting values, defaults and persistence are unchanged.

beta.56 strengthens malformed-JSON validation for P2P and Wolfx, separates JMA XML training/test messages from production, and preserves active weather-warning areas when a bulletin also reports partial releases. Unsaved reception-mode changes cannot bypass training confirmation, and profile application is blocked during OAuth operations.

beta.56 isolates deduplication, report-number ordering, and cross-provider fallback fingerprints so a training message cannot suppress matching live information. Corrupt null entries in saved display state are backed up and recovered as empty state. Legacy signatures without source/test metadata retain their production/non-test interpretation for compatibility.

beta.47: automatic JMA XML PULL fallback for supported non-EEW information after a DMDATA.JP, P2P, or AXIS connection remains faulted/reconnecting for 30 seconds. The reception settings checkbox enables or disables it (default on). The original source resumes on successful reconnection; saved source selections are unchanged. Silence or Stale alone does not trigger fallback, and partial feed outages on an otherwise connected source cannot be detected. EEW, Sandbox, disabled categories, and Wolfx are excluded. JMA polling is shared and remains at least 60 seconds apart; no backup requests are made while the selected sources are healthy. Completeness and timeliness are not guaranteed.

beta.46 adds the read-only [CDI External API v1](docs/external-api-v1.md) for earthquake, EEW, and tsunami information. It is local-only, disabled by default, and protected by a separate token. Initial synchronization does not replay captions or audio. See the external API terms below.

Beta.40 redesigns weather captions with a heading above a full-width body. Sentences and districts are kept together where possible; long text is split with place names and parentheses protected. The fixed two-line layout is removed. Designated-river forecasts retain river and district context without automatic summarization.

Beta.38 adds place-name furigana only to the telegram review window and fixes endlessly repeating OBS captions. The count includes the first pass: 2 means two complete page cycles, followed by removal. Legacy rotation/resume delays are no longer used.

### Furigana caution

**Furigana is a reading aid, not a guarantee of accuracy or completeness. Verify official pronunciations with municipal or other authoritative sources before reading names on air.** The bundled Japan Post dataset covers prefectures and municipalities; forecast regions, offshore areas, foreign places and historical names may be unsupported. Context and spelling can cause incorrect matches or readings. Unresolved names normally remain without ruby. OBS captions, replay output and audio are unchanged.

Beta.37 removes the three generic earthquake pages from large-scale eruption reports received as VXSE53 XML through AXIS and other XML sources. Captions begin with the eruption narrative and retain the full tide observations and tsunami arrival estimates. Antivirus exclusion instructions have also been removed from the distribution documentation.

- Receives and generates captions for EEW, earthquake, tsunami, weather, volcano, and Nankai Trough information
- Selects P2PQuake, DMDATA.JP, AXIS, Wolfx, JMA XML (non-EEW), or “Do not receive” separately for each supported information category
- Does not connect to a provider API when none of the selected categories require it
- Handles updates, cancellations, lifted warnings and advisories, duplicates, and superseded reports for the same event
- Shows the affected area and warning or advisory type when a warning is cleared
- Provides a separate window for reviewing live and past telegrams
- Redisplays a selected telegram in one of three modes: live-information repeat, past-information presentation, or operational training
- Provides OBS Local View and automatic browser-source registration and URL updates through OBS WebSocket 5.x
- Routes notification audio to its matching OBS category source (beta.57 onward)
- Checks AXIS token expiration and attempts renewal before expiration
- Provides logs, raw-message storage, diagnostic ZIP creation, and settings backup

## Main supported information

| Category | Main supported data |
| --- | --- |
| EEW | P2P EEW, VXSE43, VXSE45 containing a warning, AXIS `eew`, Wolfx JMA EEW |
| Earthquake | VXSE51, VXSE52, VXSE53, VXSE62, VYSE60, P2P and Wolfx JMA earthquake information |
| Tsunami | VTSE41, VTSE51, VTSE52, P2P tsunami information |
| Weather | VPWW55–61, VPWS50, VPBS50/51, VPHW50/51, VXKO50–89 (provider-dependent) |
| Volcano | VFVO50, VFVO56 |
| Nankai Trough | VYSE50 |

VPOA50 parsing remains available for past telegrams, but JMA XML and AXIS exclude it from new reception. DMDATA settings are unchanged.

Not every message delivered by a provider is converted into a caption. Messages may be excluded when they come from an unselected provider, use an unsupported format, do not meet the EEW warning criteria, are disabled by display filters, are damaged, duplicate an existing message, or have been superseded.

For weather warnings and advisories, CDI-Telopper does not replay the caption or audio when the post-filter page content is unchanged from the preceding revision in the same bulletin series. The message remains available in the received-message review with the status `No change to displayed information`.

By default, CDI-Telopper also suppresses captions and audio when every item remaining after weather filters is marked as continuing. New announcements, updates, and releases are still displayed. This behavior can be changed with the `Do not display telegrams containing only continuing items` checkbox. Audio for weather disaster-prevention bulletins (VPBS50/51) can be enabled and assigned separately from ordinary weather warning and advisory audio.

EEW audio has priority over all earthquake, tsunami, and weather audio. When an EEW arrives, pending weather audio is discarded and any currently playing affected sound is interrupted by the EEW sound. Earthquake, tsunami, and weather sounds received before the EEW sound finishes are not replayed later.

When redisplaying a telegram from the review window, choose one of three purposes. A live-information repeat badge shows only the telegram's issue time; past-information and training badges show both the purpose and issue time. Only telegrams actually received in production can use the live-information repeat mode; messages loaded from a history provider cannot be presented as live repeats.

## Reception providers

### JMA XML (direct PULL feed)

An unauthenticated provider that retrieves XML directly from JMA's public feeds. CDI-Telopper is not an official JMA application.

- Supports the application's existing earthquake, tsunami, weather (including designated-river floods), volcano and Nankai Trough categories. **EEW is excluded.**
- Select “気象庁XML（60秒巡回・遅延あり）” per category and save. Separately from manual selection, beta.47 and later support connection-failure fallback under the conditions described above.
- Keeps polling starts at least 60 seconds apart. Weather uses `extra.xml`; earthquake, tsunami, volcano and Nankai Trough share `eqvol.xml`. Each required feed is checked once per cycle, regardless of how many categories select it. New telegram bodies require separate requests.
- Excludes `VPWW53`, `VPWW54` and `VPOA50`. Downloaded URLs are deduplicated within the retained history; failed downloads are retried on a later cycle.
- Publication can be delayed or interrupted. Pre-connection telegrams are not presented as new alerts, and long outages are not fully backfilled. “Connected” indicates successful polling, not a guarantee of new messages or captions.

[JMA feed documentation and usage notes](https://xml.kishou.go.jp/xmlpull.html)

### P2PQuake API

P2PQuake is primarily used for EEW, earthquake, and tsunami information. Production and Sandbox connections are available.

The P2PQuake API is not an official API operated by JMA. Treat it as an external service carrying JMA-issued information, use official information alongside it, and review the provider's terms, rate limits, and secondary-use conditions.

### DMDATA.JP

You must provide your own subscription and either an API key or OAuth2.0 authorization. For EEW, select either a warning subscription (VXSE43) or forecast subscription (VXSE45), according to your contract. With a forecast subscription, CDI-Telopper displays VXSE45 messages that contain a warning, as well as their cancellations.

Since beta.55, OAuth2.0 browser authorization is available alongside API-key authentication. CDI's public client ID is embedded, so normal users do not need to register a client or enter an ID. Select OAuth2.0, authorize your own account in the browser, and save the settings. Accounts, contracts and tokens are not shared. Tokens are protected with Windows DPAPI CurrentUser and refreshed when needed for API requests such as reconnection. See the [OAuth setup guide](docs/dmdata-oauth.md) for connection, scopes and revocation.

Since beta.56, “契約情報を更新” in Reception settings retrieves contract information on demand. It requires `contract.list` on the API key or an additional OAuth authorization, with the scope enabled in the client registration. The embedded client's permission configuration is unverified; contract retrieval is not guaranteed to succeed. Normal reception authorization continues to request its existing scopes.

The About tab shows the actual application version, build information, DMDATA website, GitHub repository, releases, policies and contact email. DMDATA reception settings also include a direct link to the official website. Links open in the default browser or mail application.

### AXIS

AXIS is treated as an experimental provider. You must provide a valid access token and have access to the required channels. Depending on the selected categories, CDI-Telopper uses `eew`, `jmx-seismology`, `jmx-meteorology`, and `jmx-volcanology`.

### Wolfx

Wolfx provides public WebSocket endpoints that require no authentication. It can be selected for EEW and earthquake information. CDI-Telopper accepts warning-level EEW messages and cancellations and ignores forecast-only EEW updates. Wolfx is not an official API operated by a government or meteorological agency; use official information alongside it and follow the provider's terms and connection limits.

**Important: Wolfx earthquake information currently contains only “Hypocenter and seismic intensity information,” equivalent to VXSE53.** It does not provide VXSE51 seismic intensity prompt reports, VXSE52 hypocenter reports, VXSE62 long-period ground motion reports, or the other earthquake telegram types supported through other providers. CDI-Telopper normalizes the latest item in each Wolfx earthquake-list update. Wolfx is an unofficial source; use official JMA information alongside it and confirm the provider's current terms and connection limits.

https://wolfx.jp/docs/open-api/

DMDATA.JP and AXIS credentials are encrypted using Windows DPAPI CurrentUser. The application is designed not to include plaintext credentials in source files, release packages, logs, or diagnostic ZIP files.

## OBS output

Designated-river flood forecasts (VXKO50–89) use the weather provider and output. Captions show the river headline, station-grouped potential inundation districts, then the telegram's main narrative for levels 4–5. Headings and body text are separated; only long passages are split. Level 2 uses advisory settings, levels 3–4 warning settings, and level 5 special-warning settings. Disabling advisories also hides level 2 flood forecasts.

Create the following four browser sources in OBS. Each source is designed for a 1920×1080 canvas.

- CDI-Telopper 地震字幕 (earthquake captions and audio)
- CDI-Telopper 緊急地震速報 (EEW)
- CDI-Telopper 津波字幕 (tsunami captions)
- CDI-Telopper 気象情報 (weather information)

In beta.57 onward, enable “Control audio via OBS” for all four sources. Each category plays audio through its own source; the browser preview stays silent. OBS WebSocket synchronization enables audio control for all four, sets monitoring off, creates missing sources, updates session URLs and renames the legacy “CDI-Telopper 地震字幕・全ての音声” source to “CDI-Telopper 地震字幕”. Do not register both legacy and new sources. The downloadable beta.56 still sends all audio through its earthquake source.

Legacy OBS map output and the always-on desktop overlay have been removed. The built-in experimental map has also been removed. Use the silent browser preview and the live/past telegram review window for on-PC confirmation.

## System requirements

- Windows 10 or Windows 11, 64-bit
- OBS Studio 28 or later recommended
- Internet access to the selected reception providers
- A valid subscription and credentials when using AXIS or DMDATA.JP

GitHub Release packages are self-contained .NET 8 builds. Visual Studio and the .NET SDK are not required for normal use.

## Installation and first launch

1. Download a ZIP package from [Releases](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases).
2. Optionally verify it against `SHA256SUMS.txt`.
3. Fully extract the ZIP into a normal writable directory. Do not run the application directly from inside the ZIP.
4. Start `CDI-Telopper.exe`.
5. Configure a provider for each category, credentials, OBS, audio, and display conditions.
6. Verify the test output before selecting **Connect**.

Multiple simultaneous instances are prevented. Closing the main window with the X button minimizes the application to the system tray instead of terminating it. To exit completely, right-click the tray icon and select **終了** (Exit).

For compatibility, settings, state, and logs are stored under `%LOCALAPPDATA%\QTelopper\2.x-beta` by default. Use `QTELOPPER_V2_BETA_DATA_DIRECTORY` to select another location.

## Security notes

- Never publish your AXIS token, DMDATA.JP API key, or OBS WebSocket password.
- Verify the Release page and SHA-256 values before extracting a package.
- Confirm subscription, redistribution, and concurrent-connection terms with each external provider.

## Verifying the source

Building requires Windows 10/11 x64, the .NET 8 SDK, and PowerShell. Visual Studio is not required.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

The script restores dependencies, builds every project in the Release configuration, and runs the automated tests.

To create distributable packages, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.57
```

The folder package, single-file package, `version.json`, and `SHA256SUMS.txt` are written to `artifacts\release\2.0.0-beta.57\win-x64`. See [SOURCE_BUILD.md](SOURCE_BUILD.md) for details.

## External API integration: terms and disclaimer

The following conditions apply to the external API integration feature and data obtained through it:

- Do not use the feature or its data for commercial purposes.
- Do not transmit the obtained data over the Internet, except for video uploads and similar activities performed manually by the user.
- Do not duplicate or redistribute any or all of the obtained data through relays or similar mechanisms.
- When using connected software for video production, publication, or streaming (including YouTube and other live streams), clearly state that CDI-Telopper is used. Attribution does not authorize commercial use or otherwise prohibited transmission or redistribution.
- Do not use the feature in public or commercial facilities, to control machine tools or medical equipment, or in other systems affecting human life or physical safety.
- Comply with the source APIs' and data providers' terms, contracts, and reuse and redistribution restrictions. These conditions do not relax provider restrictions or grant permission for prohibited uses.

This feature is provided as is, without any warranty of operation, compatibility, accuracy, completeness, timeliness, continuity, or fitness for a particular purpose. To the extent permitted by applicable law, the developer accepts no liability for damage arising from its use or inability to use it. Users are responsible for testing their setup and must also consult official disaster information.

## License and attribution

- Application license: [LICENSE](LICENSE)
- Data sources and history retrieval: [docs/data-sources.md](docs/data-sources.md)
- Audio libraries and user-provided audio: [docs/assets-license.md](docs/assets-license.md)

User-selected audio files are not included in the repository or release packages.
