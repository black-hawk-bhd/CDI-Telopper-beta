# CDI-Telopper

English | [日本語](README.md)

**Comprehensive Disaster Information Telopper (CDI-Telopper)** is a Windows application that receives earthquake early warnings and reports on earthquakes, tsunamis, weather, and volcanic activity. It sends captions and notification sounds to OBS.

The current public release is **2.0.0-beta.76** (November 26, 2026). This is a development beta. Before using it in a live broadcast, thoroughly test reception, reconnection, OBS output, audio, cancellations, and the lifting of warnings and advisories in your own environment. Do not rely on this application as your sole source for safety decisions. Always confirm critical information through official sources such as the Japan Meteorological Agency (JMA).

- [Download 2.0.0-beta.76](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/tag/v2.0.0-beta.76)
- [English user guide](docs/USER_GUIDE_EN.md) / [Japanese beta.76 release guide](README_CDI-Telopper_2.0.0-beta.76.txt)
- [Release history from beta.64 to beta.76](#release-history) / [Japanese beta.76 release notes](RELEASE_NOTES_2.0.0-beta.76.md)
- [Build from source](SOURCE_BUILD.md)

## Main features

- **Reception and display filters.** Choose a provider for each information category and filter on region, seismic intensity, or warning type. [Main supported information](#main-supported-information)
- **OBS output.** Use OBS WebSocket to register four browser sources for earthquake and volcano information, EEW, tsunamis, and weather. Notification sounds play through the matching source. Each category can display a transparent PNG alongside its captions. [OBS output](#obs-output)
- **Caption settings.** Choose an installed font and set the page duration by bulletin type. With tsunami captions enabled and an active tsunami advisory or warning for the same earthquake, you can omit the standard caution sentence from a seismic intensity bulletin or hide the entire bulletin. [Reception and caption settings](docs/USER_GUIDE_EN.md#set-up-reception-and-captions)
- **Message review and rehearsal.** Review and redisplay received messages, open a silent browser preview, or run a Disaster Simulator exercise. Bulk-import the 185 bundled JMA sample XML files and filter the test library by category or bulletin type. [Review and rehearsal](docs/USER_GUIDE_EN.md#review-past-messages-and-run-rehearsals)
- **Diagnostics and integration.** Save logs and raw bulletins, back up settings, and create diagnostic ZIPs. Use the CDI External API to connect applications running on the same PC. [External API setup](docs/USER_GUIDE_EN.md#connect-the-external-api)

## What's new in beta.76

- **Earthquake, tsunami, and volcano captions.** The first line of text aligns vertically with the badge. Long earthquake and river flood reports prefer Japanese sentence endings (`。`) when choosing page breaks. Overseas tsunami observations use one row per station, such as `チリ・コラル 0.9 m`, with up to two stations per page.
- **Reception and display.** The reception settings now name the Hokkaido and Sanriku offshore subsequent earthquake advisory (VYSE60). The display filters have individual controls for Nankai Trough earthquake information and the Hokkaido and Sanriku advisory. [Reception and caption settings](docs/USER_GUIDE_EN.md#set-up-reception-and-captions)
- **Volcano sounds and OBS controls.** Configure separate notification sounds for eruption warnings and forecasts (VFVO50) and eruption flashes (VFVO56) under **音声** → **火山情報** (Audio → Volcano information). Repeated comparison tests evaluate sound playback anew and log why a sound was skipped. The OBS URL copy buttons now use consistent labels for earthquake and volcano, tsunami, and weather captions.
- **Test library.** Fixed missing rehearsal pages for some tsunami (VTSE41), designated-river flood, and weather (VPWW61) bulletins. The library supports XML and JSON file registration and bulk XML folder registration. DMDATA archive registration, test-case ZIP import and export, and reference PNG attachments were retired.
- **Disaster Simulator.** When a seismic intensity bulletin and a tsunami advisory or warning share an event ID, the options to omit the standard caution or hide the entire bulletin also apply in rehearsal. With bulletin suppression enabled, a later tsunami advisory or warning removes the matching seismic intensity bulletin. [Simulator guide in Japanese](docs/disaster-simulator.md)
- **Telegram review.** Removed the setting that hides the mode banner and shortened the replay settings labels.
- **Optional warning details.** The preview and OBS can display the forecast maximum intensity and estimated magnitude supplied by the source. The option defaults to off. A cancellation removes the estimates for the matching warning.
- **Settings window.** The interface uses the full Japanese term for earthquake early warning. Settings panels are limited to 960 DIPs and fit narrower windows.

## Release history

### beta.64–beta.76

beta.76 adds optional EEW estimates and volcano notification sounds, and improves captions and rehearsals. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.76.md)

beta.75 adds PNG images alongside OBS captions, bulk import of training XML folders, and test-library filters by category and bulletin type. Both packages include 185 JMA sample XML files. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.75.md) · [JMA sample attribution](jmaxml_20261118_CDI-Telopper/README.md)

beta.74 adds per-bulletin page durations, caption font selection, a portable `Settings` folder next to the executable, and suppression of seismic intensity bulletins while a tsunami advisory or warning for the same earthquake is active. Weather warnings and danger warnings have individual filters. The caption option for information types applies to live output, training, tests, rehearsals, and replays. EEW follow-up highlighting applies to areas present in the previous report whose forecast intensity, arrival time, or warning status changed. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.74.md)

beta.73 adds options to highlight new or updated areas in EEW follow-up reports, omit the provisional caution from a seismic intensity bulletin when a tsunami advisory or warning for the same earthquake is active, and hide non-JMA intensity stations. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.73.md)

beta.72 aligns the first location line vertically with its intensity badge and removes repeated advisory sentences within an earthquake bulletin. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.72.md)

beta.71 adds backoff and clearer connection logs for Wolfx, adds separate controls for routine Nankai Trough commentary, and aligns earthquake and tsunami captions. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.71.md)

beta.70 corrects the Wolfx settings help to list the supported reception categories and explain how earthquake details are retrieved. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.70.md)

beta.69 adds Wolfx support for tsunami information, special earthquake information for the Nankai Trough and the Hokkaido and Sanriku offshore regions, and detailed seismic intensity reports. Earthquake Early Warning follow-up audio plays when the displayed area names change. YDITS reception is temporarily suspended. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.69.md)

beta.68 adds optional reception API connections at startup and per-provider status in the main window and notification area. Starting live reception stops running tests and blocks new tests. Operators can still redisplay past or previously received messages from the review window. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.68.md)

beta.67 adds JMA readings for seismic observation stations, seismic regions and tsunami forecast areas. Open **震度観測点（電文）** in the review window to see the source station names and their readings. [Japanese release notes](RELEASE_NOTES_2.0.0-beta.67.md)

beta.66 adds optional External API startup activation, encrypted connection-key storage and rotation, and a fixed port shared with OBS Local View. A saved key and a fixed port let local clients reuse the same connection URL after a restart. See the [Japanese release notes](RELEASE_NOTES_2.0.0-beta.66.md).

beta.65 adds YDITS public EEW warnings and a separate preview-only test mode. Follow-up audio for the same warning waits for the current playback to finish. Cancellation audio interrupts and clears pending follow-ups. The earthquake API now includes the source observation-point name in `points[].address`. See the [release notes](RELEASE_NOTES_2.0.0-beta.65.md).

beta.64 adds a confirmation-protected reset of all saved settings under About / Reset settings. Selecting Yes saves and applies defaults, clearing stored API keys, AXIS tokens, OBS passwords and audio-file selections. Archived telegrams, logs, test data, profiles and DMDATA OAuth authorization remain intact. NII archive requests now have a 60-second deadline covering headers and body, with one delayed retry for transient failures and reuse of successfully cached XML. All 1,128 automated tests pass with zero build warnings/errors. See the [release notes](RELEASE_NOTES_2.0.0-beta.64.md) and [validation results](VALIDATION_2.0.0-beta.64.md).

### beta.63 and earlier

beta.63 migrates all 15 projects to .NET 10 and bundles runtime 10.0.14 in both distribution formats. It addresses CVE-2026-111497, disables Brotli for the JMA startup tsunami snapshot, and rejects unsupported content encodings before reading the body. Self-contained restore/publish settings and bundled-runtime checks are aligned. Vertical EEW cards can now be left-aligned, with right alignment remaining the default. All 1,096 automated tests, seven runtime guard cases and 13 Chrome rendering checks pass with zero build warnings/errors. Live reception and real OBS checks remain unperformed. See the [release notes](RELEASE_NOTES_2.0.0-beta.63.md), [validation results](VALIDATION_2.0.0-beta.63.md) and [migration notes](docs/dotnet10-migration.md).

beta.62 adds an optional vertical EEW warning layout under Display / Pages and text. It defaults off, keeping the existing horizontal layout. The browser monitor and OBS share the same existing colors and bold Gothic font, with separate cards for two independent warnings and their cancellations. Multiple Hokkaido display labels, including Rishiri/Rebun and Okushiri, now collapse to one Hokkaido label in both layouts. Single labels, other regional aggregation rules and original region data are preserved. All 1,077 automated tests and 10 Chrome rendering checks pass with zero build warnings/errors. Live reception and real OBS checks remain unperformed. See the [layout guide](docs/eew-vertical-style.md), [release notes](RELEASE_NOTES_2.0.0-beta.62.md) and [validation results](VALIDATION_2.0.0-beta.62.md).

beta.61 updates the bundled runtime to .NET 8.0.32 and rejects older self-contained runtime packs. Test-case ZIP imports now enforce compressed, entry-count and expanded-size budgets. AXIS gap recovery enforces streamed response limits and deadlines. Reception mergers and the AXIS recovery stream apply bounded, cancellable backpressure rather than silently dropping accepted messages. Existing settings, display behavior and live/training metadata remain intact. All 1,052 automated tests pass with zero build warnings/errors. Live-provider and real OBS visual checks remain unperformed. See the [release notes](RELEASE_NOTES_2.0.0-beta.61.md) and [validation results](VALIDATION_2.0.0-beta.61.md).

beta.60: the two-row weather warning/advisory area-list setting now covers all statuses, including releases. Mixed announcements retain separate status pages and repeat prefecture/status on continuation pages. The default remains on; switching it off restores three rows. Narrative pages, names per row, audio and source telegrams are unchanged. All 999 automated tests pass with zero build warnings/errors; live reception and real OBS visual checks remain unperformed. See the [release notes](RELEASE_NOTES_2.0.0-beta.60.md) and [validation results](VALIDATION_2.0.0-beta.60.md).

beta.60: the plain-subtitle alternative to information badges now also applies to past-information replay, training, rehearsal, Simulator, Sandbox and automatic production rotation. Enabling the corresponding mode's option keeps type/intensity text while removing the decorated badge. Normal and rehearsal choices remain independent; training metadata and the separate top-banner option are unchanged.

beta.59 adds manual historical JMA XML retrieval from the official long feeds (several days, hourly updates; no EEW), with selected replay through the review window. Normal-mode badge replacement keeps information types and intensity classes visible as plain subtitle text. Separate replay/training badge settings and an optional final announcement-time page are available. All 978 automated tests pass; real-server retrieval and real OBS visual checks remain unperformed. See the [release notes](RELEASE_NOTES_2.0.0-beta.59.md) and [validation results](VALIDATION_2.0.0-beta.59.md).

beta.58: a newer, explicit EEW cancellation may be accepted despite a lower serial number, without rewriting source fields. Source message time, event identity and live/test separation govern the exception; stale reports cannot resurrect a cancelled warning. Ordering/cancellation state is persisted within bounded history. JMA XML uses `Control/DateTime`, or an explicit header time only when Control time is absent, never local receipt time. Ambiguous regressions, inconsistent XML cancellation metadata and source times more than five minutes ahead of receipt are held for review. Accepted anomalies and review cases are archived locally with `_ordering-review` filenames even when ordinary raw-message logging is off, under its retention/size limits. Captions, audio and the external API share the ingestion decision. Legacy snapshots without ordering metadata do not establish historical ordering.

All 926 automated tests pass for beta.58. Actual live reception and real OBS checks remain unperformed. See the [release notes](RELEASE_NOTES_2.0.0-beta.58.md) and [validation results](VALIDATION_2.0.0-beta.58.md).

beta.57 replaces the WPF preview with the silent browser preview, showing earthquake, EEW, tsunami and weather panels together. Repeated automatic launches of the same URL are suppressed.

beta.57 adds individual exclusions for earthquake maximum intensities 1 through 6-upper, selectable prefecture filtering for events and/or points, and an option to include intensity 1–2 points in earthquakes with a maximum of 3 or higher (default off). Intensity 7, unknown/unreported intensity and cancellations are not excluded by intensity selection. Source data and external API values remain unchanged.

beta.58 adds an opt-in filter setting that keeps earthquake information containing the normalized “EEW was issued, but no strong shaking was observed” explanation visible despite maximum-intensity exclusions. It defaults off, remains subject to the earthquake category and prefecture settings, and does not classify the earthquake or EEW as cancelled. Enable it in Filters and save/apply. It affects subsequent processing, not automatic redisplay of previously hidden messages.

beta.57 adds per-category audio timing: legacy playback (default), first new announcement only, or new announcements and escalations. Classification uses the announcements observed during the session, with training and production kept separate; it cannot guarantee the true first announcement when starting mid-event or when metadata is missing.

beta.57 routes earthquake, EEW, tsunami and weather audio through their matching OBS browser sources, including audio tests. All four sources require OBS audio control. The 883 automated tests pass; actual reception and visual/audio checks with real OBS remain unperformed. See the [change notes](RELEASE_NOTES_2.0.0-beta.57.md) and [validation results](VALIDATION_2.0.0-beta.57.md).

beta.56 adds manual DMDATA.JP contract-information retrieval in reception settings. It displays each returned plan's contract status, daily price and monthly maximum, start time, and additional connection allowance; these prices are not an actual bill. Retrieval failures are not reported as uncontracted plans. Contract information is shown temporarily in the control window, not saved to files, settings, logs or diagnostic ZIPs, and not sent to OBS captions or the external API. There is no periodic polling or contract modification. API keys need `contract.list`; OAuth users must explicitly authorize that additional scope with “契約情報も認可”. It must also be permitted in the registered OAuth client. The embedded client's registration for this scope and retrieval with a real account have not been verified. See the [contract-information setup guide](docs/dmdata-oauth.md#契約情報の表示).

beta.56 separates settings into “受信” (Reception), “フィルター” (Filters), “表示” (Display), and “出力” (Output). Reception contains providers and authentication; Filters selects the information to display; Display controls pages, text and repetitions; Output contains canvas, OBS and external API settings. Setting values, defaults and persistence are unchanged.

beta.56 strengthens malformed-JSON validation for P2P and Wolfx, separates JMA XML training/test messages from production, and preserves active weather-warning areas when a bulletin also reports partial releases. Unsaved reception-mode changes cannot bypass training confirmation, and profile application is blocked during OAuth operations.

beta.56 isolates deduplication, report-number ordering, and cross-provider fallback fingerprints so a training message cannot suppress matching live information. Corrupt null entries in saved display state are backed up and recovered as empty state. Legacy signatures without source/test metadata retain their production/non-test interpretation for compatibility.

beta.53 displays active weather warning and advisory area names in two rows per page by default. The third row moves to a new page with its prefecture and status repeated. A checkbox restores the prior three-row layout. The release also updates the EXE, window, and tray icon and the control-window logo.

beta.52 adds a silent browser preview monitor with four output panels and independent telegram review. Enable OBS Local View and use the browser-monitor button. Chrome is preferred, with the default browser as fallback. Keep the authenticated URL private.

beta.51 adds per-domain upstream health, a separately authenticated rehearsal API, Simulator disconnect-state retention, and optional intensity-class-separated pages enabled by default. [External API specification](docs/external-api-v1.md)

beta.50 improves unreported-intensity normalization and labeling and preserves observed and unreported entries at the same place. Trailing full-width station markers are stripped from display text. The release also improves Simulator decoding and adds external API fields.

beta.49 restores long-period ground-motion classes and areas from Simulator messages that supply `longPeriodIntensity`.

beta.48 removes the built-in map and adds a training connection for Disaster Simulator 0.3.1. Earthquake, EEW, and tsunami updates received from the Simulator are always treated as training. [Simulator guide in Japanese](docs/disaster-simulator.md)

beta.47: automatic JMA XML PULL fallback for supported non-EEW information after a DMDATA.JP, P2P, or AXIS connection remains faulted/reconnecting for 30 seconds. The reception settings checkbox enables or disables it (default on). The original source resumes on successful reconnection; saved source selections are unchanged. Silence or Stale alone does not trigger fallback, and partial feed outages on an otherwise connected source cannot be detected. EEW, Sandbox, disabled categories, and Wolfx are excluded. JMA polling is shared and remains at least 60 seconds apart; no backup requests are made while the selected sources are healthy. Completeness and timeliness are not guaranteed.

beta.46 adds the read-only [CDI External API v1](docs/external-api-v1.md) for earthquake, EEW, and tsunami information. It is local-only, disabled by default, and protected by a separate token. Initial synchronization does not replay captions or audio. See the external API terms below.

beta.45 unifies non-river weather headings across continuation pages, preview, OBS, and telegram review. The information type appears in the badge, the area alongside it, and the status when the bulletin supplies one. [Weather layout guide in Japanese](docs/weather-heading-layout.md)

beta.43 puts the information type in the weather badge, the prefecture and status alongside it, and municipalities below. Tsunami observation introductions and active warning and advisory announcements use individual pages.

beta.41 fixes JMA XML telegrams being rejected before normalization. JMA XML is an unauthenticated pull provider for supported categories other than EEW. [JMA usage notes](https://xml.kishou.go.jp/xmlpull.html)

Beta.38 adds place-name furigana only to the telegram review window and fixes endlessly repeating OBS captions. The count includes the first pass: 2 means two complete page cycles, followed by removal. Legacy rotation/resume delays are no longer used.

### Furigana caution

**Furigana is a reading aid, not a guarantee of accuracy or completeness. Verify official pronunciations with municipal or other authoritative sources before reading names on air.** The telegram review window uses JMA code tables and Japan Post readings for prefectures and municipalities. It displays furigana when it can identify a reading. [Dictionary coverage and attribution](docs/data-sources.md#place-name-ruby-in-telegram-review)

Beta.37 removes the three generic earthquake pages from large-scale eruption reports received as VXSE53 XML through AXIS and other XML sources. Captions begin with the eruption narrative and retain the full tide observations and tsunami arrival estimates. Antivirus exclusion instructions have also been removed from the distribution documentation.

## Main supported information

| Category | Main supported data |
| --- | --- |
| EEW | P2P EEW, VXSE43 (DMDATA.JP), VXSE45 containing a warning, AXIS `eew`, Wolfx JMA EEW |
| Earthquake | VXSE51, VXSE52, VXSE53, VXSE62, VYSE60 (DMDATA.JP and AXIS), P2P and Wolfx JMA earthquake information |
| Tsunami | VTSE41, VTSE51, VTSE52, P2P tsunami information |
| Weather | VPWW55–61, VPWS50, VPBS50/51, VPHW50/51, VXKO50–89 (provider-dependent) |
| Volcano | VFVO50, VFVO56 |
| Nankai Trough and Hokkaido/Sanriku | VYSE50, VYSE51/52 (Wolfx), VYSE60 (JMA XML and Wolfx) |

VPOA50 parsing remains available for past telegrams, but JMA XML and AXIS exclude it from new reception. DMDATA settings are unchanged.

Not every message delivered by a provider is converted into a caption. Messages may be excluded when they come from an unselected provider, use an unsupported format, do not meet the EEW warning criteria, are disabled by display filters, are damaged, duplicate an existing message, or have been superseded.

For weather warnings and advisories, CDI-Telopper does not replay the caption or audio when the post-filter page content is unchanged from the preceding revision in the same bulletin series. The message remains available in the received-message review with the status `No change to displayed information`.

By default, CDI-Telopper also suppresses captions and audio when every item remaining after weather filters is marked as continuing. New announcements, updates, and releases are still displayed. This behavior can be changed with the `Do not display telegrams containing only continuing items` checkbox. Audio for weather disaster-prevention bulletins (VPBS50/51) can be enabled and assigned separately from ordinary weather warning and advisory audio.

EEW audio has priority over all earthquake, tsunami, and weather audio. When an EEW arrives, pending weather audio is discarded and any currently playing affected sound is interrupted by the EEW sound. Earthquake, tsunami, and weather sounds received before the EEW sound finishes are not replayed later.

When redisplaying a telegram from the review window, choose one of three purposes. A live-information repeat badge shows only the telegram's issue time; past-information and training badges show both the purpose and issue time. Only telegrams actually received in production can use the live-information repeat mode; messages loaded from a history provider cannot be presented as live repeats.

Enable **情報種別をバッジではなく字幕で表示** under **表示** (Display) to show the information type or seismic intensity as text in live output, training, tests, rehearsals, and replays. The default is off. With the main setting off, training, rehearsals, and replays use the setting in the telegram review window.

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

The Wolfx public APIs used here require no authentication. Select Wolfx under **受信** (Reception) → **受信元** (Sources) for EEW, earthquake information, tsunami information, or the shared Nankai Trough and Hokkaido/Sanriku row.

- Tsunami reception covers VTSE41 warnings, advisories and forecasts, plus VTSE51 and VTSE52 observations. The tsunami display honors the source's trust deadline and handles observation cancellations independently of the warning.
- Special earthquake reception covers Nankai Trough reports VYSE50, VYSE51 and VYSE52, plus the Hokkaido and Sanriku subsequent earthquake advisory VYSE60. Reports already published at connection time are available in telegram review.

Earthquake reception uses WebSocket summaries and polls the Earthquake Detail API at intervals of at least 30 seconds. Reports VXSE51, VXSE52, VXSE53, VXSE61 and VXSE62 can supply official publication times, hypocenters, regional and station intensities, or long-period ground motion classes. The official event ID links summaries and details. Reports already published at connection time are available in telegram review.

https://wolfx.jp/docs/open-api/

### YDITS (temporarily suspended)

YDITS production and test reception are temporarily suspended. Settings and profiles that previously selected YDITS use **受信しない** (Do not receive) for EEW. To receive EEW, select another source under **受信** (Reception) → **受信元** (Sources) → **EEW**.

DMDATA.JP and AXIS credentials are encrypted using Windows DPAPI CurrentUser. The application is designed not to include plaintext credentials in source files, release packages, logs, or diagnostic ZIP files.

## OBS output

Designated-river flood forecasts (VXKO50–89) use the weather provider and output. Captions show the river headline, station-grouped potential inundation districts, then the telegram's main narrative for levels 4–5. Headings and body text are separated; only long passages are split. Level 2 uses advisory settings, levels 3–4 warning settings, and level 5 special-warning settings. Disabling advisories also hides level 2 flood forecasts.

Create the following four browser sources in OBS. Each source is designed for a 1920×1080 canvas.

- CDI-Telopper 地震字幕 (earthquake captions and audio)
- CDI-Telopper 緊急地震速報 (EEW)
- CDI-Telopper 津波字幕 (tsunami captions)
- CDI-Telopper 気象情報 (weather information)

In beta.57 onward, enable “Control audio via OBS” for all four sources. Each category plays audio through its own source and the browser preview stays silent. OBS WebSocket synchronization enables audio control for all four, sets monitoring off, creates missing sources, updates session URLs and renames the legacy “CDI-Telopper 地震字幕・全ての音声” source to “CDI-Telopper 地震字幕”. Do not register both legacy and new sources.

Legacy OBS map output and the always-on desktop overlay have been removed. The built-in experimental map has also been removed. Use the silent browser preview and the live/past telegram review window for on-PC confirmation.

## System requirements

- Windows 10 or Windows 11, 64-bit
- OBS Studio 28 or later recommended
- Internet access to the selected reception providers
- A valid subscription and credentials when using AXIS or DMDATA.JP

GitHub Releases lists the self-contained single-EXE package first. The lightweight package requires the x64 .NET 10 Desktop Runtime at version 10.0.14 or a later .NET 10 patch. [Microsoft downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Installation and first launch

1. Download a ZIP package from [Releases](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases).
2. Optionally verify it against `SHA256SUMS.txt`.
3. Fully extract the ZIP into a normal writable directory. Do not run the application directly from inside the ZIP.
4. Start `CDI-Telopper.exe`.
5. Configure a provider for each category, credentials, OBS, audio, and display conditions. Select **保存して反映** (Save and apply).
6. Verify the test output before selecting **Connect**.

Multiple simultaneous instances are prevented. Closing the main window with the X button minimizes the application to the system tray instead of terminating it. To exit completely, right-click the tray icon and select **終了** (Exit).

Settings, state, and logs are stored under `%LOCALAPPDATA%\QTelopper\2.x-beta` by default. Under **バージョン情報** → **保存先** (About → Storage location), you can select a `Settings` folder beside the executable. The change applies from the next launch and covers newly saved XML and JSON telegrams. The `QTELOPPER_V2_BETA_DATA_DIRECTORY` environment variable takes precedence when set.

To update, close the previous version and extract the new ZIP to a separate folder. If you use a portable `Settings` folder, copy it beside the new executable before launching the new version.

## Security notes

- Never publish your AXIS token, DMDATA.JP API key, or OBS WebSocket password.
- Verify the Release page and SHA-256 values before extracting a package.
- Confirm subscription, redistribution, and concurrent-connection terms with each external provider.

## Verifying the source

The current development source targets .NET 10. Building requires Windows 10/11 x64, .NET 10 SDK 10.0.403 or later, and PowerShell. Visual Studio is not required. Consult each release description for the runtime bundled with an already published package.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

The script restores dependencies, builds every project in the Release configuration, and runs the automated tests.

To create distributable packages, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.76
```

Starting with beta.75, each release includes a self-contained single-EXE ZIP and a lightweight ZIP. The ZIP files, `version.json`, and `SHA256SUMS.txt` are written to `artifacts\release\2.0.0-beta.76\win-x64`. [Source build details](SOURCE_BUILD.md)

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

The application source code is published under the MIT License. Section 4 of the terms covers the External API integration and data obtained through it. Third-party components, data and audio remain subject to their respective providers' conditions. [MIT License](LICENSE) / [Terms and disclaimer](TERMS_EN.md) / [Privacy Policy](PRIVACY_EN.md) / [Data sources](docs/data-sources.md) / [Third-party notices and audio](docs/assets-license.md)

User-selected audio files are not included in the repository or release packages.
