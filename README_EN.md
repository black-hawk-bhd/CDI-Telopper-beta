# CDI-Telopper

Beta.42: excludes legacy VPWW53/VPWW54/VPOA50 from JMA XML and VPOA50 from AXIS; groups weather captions under shared type/prefecture/status headings with separate status pages; adds category filtering to the telegram review window.

The experimental map is disabled in normal and distribution builds. Its buttons remain disabled; source, geographic data and tests are retained for development. Beta.42 distribution packages also disable the map.

English | [日本語](README.md)

Beta.41 fixes JMA XML telegrams being rejected before normalization. “気象庁XML” is a manually selected, unauthenticated JMA XML pull provider for existing supported non-EEW categories. Select it in reception settings and save. Polling occurs at intervals of at least 60 seconds. Telegrams predating the initial connection are not displayed as new alerts. Publication may be delayed or interrupted; long outages may cause missed messages. There is no automatic AXIS failover. See [JMA's usage notes](https://xml.kishou.go.jp/xmlpull.html).

**Comprehensive Disaster Information Telopper (CDI-Telopper)** is a Windows application that receives disaster information related to earthquakes, tsunamis, weather, volcanoes, and the Nankai Trough, then generates captions for OBS.

The current public release is **2.0.0-beta.42**. This is a development beta. Before using it in a live broadcast, thoroughly test reception, reconnection, OBS output, audio, cancellations, and the lifting of warnings and advisories in your own environment. Do not rely on this application as your sole source for safety decisions. Always confirm critical information through official sources such as the Japan Meteorological Agency (JMA).

- [Download 2.0.0-beta.42](https://github.com/black-hawk-bhd/CDI-Telopper-beta/releases/tag/v2.0.0-beta.42)
- [Detailed Japanese manual and specification](README_CDI-Telopper_2.0.0-beta.42.txt)
- [Build from source](SOURCE_BUILD.md)

## Main features

Beta.40 redesigns weather captions with a heading above a full-width body. Sentences and districts are kept together where possible; long text is split with place names and parentheses protected. The fixed two-line layout is removed. Designated-river forecasts retain river and district context without automatic summarization.

Beta.38 adds place-name furigana only to the telegram review window and fixes endlessly repeating OBS captions. The count includes the first pass: 2 means two complete page cycles, followed by removal. Legacy rotation/resume delays are no longer used.

### Furigana caution

**Furigana is a reading aid, not a guarantee of accuracy or completeness. Verify official pronunciations with municipal or other authoritative sources before reading names on air.** The bundled Japan Post dataset covers prefectures and municipalities; forecast regions, offshore areas, foreign places and historical names may be unsupported. Context and spelling can cause incorrect matches or readings. Unresolved names normally remain without ruby. OBS captions, replay output and audio are unchanged.

Beta.37 removes the three generic earthquake pages from large-scale eruption reports received as VXSE53 XML through AXIS and other XML sources. Captions begin with the eruption narrative and retain the full tide observations and tsunami arrival estimates. Antivirus exclusion instructions have also been removed from the distribution documentation.

- Receives and generates captions for EEW, earthquake, tsunami, weather, volcano, and Nankai Trough information
- Selects P2PQuake, DMDATA.JP, AXIS, Wolfx, or “Do not receive” separately for each supported information category
- Does not connect to a provider API when none of the selected categories require it
- Handles updates, cancellations, lifted warnings and advisories, duplicates, and superseded reports for the same event
- Shows the affected area and warning or advisory type when a warning is cleared
- Provides a separate window for reviewing live and past telegrams
- Redisplays a selected telegram in one of three modes: live-information repeat, past-information presentation, or operational training
- Provides OBS Local View and automatic browser-source registration and URL updates through OBS WebSocket 5.x
- Consolidates notification audio into a dedicated OBS source
- Checks AXIS token expiration and attempts renewal before expiration
- Provides logs, raw-message storage, diagnostic ZIP creation, and settings backup

## Main supported information

| Category | Main supported data |
| --- | --- |
| EEW | P2P EEW, VXSE43, VXSE45 containing a warning, AXIS `eew`, Wolfx JMA EEW |
| Earthquake | VXSE51, VXSE52, VXSE53, VXSE62, VYSE60, P2P and Wolfx JMA earthquake information |
| Tsunami | VTSE41, VTSE51, VTSE52, P2P tsunami information |
| Weather | VPWW55–61, VPOA50, VPBS50/51, VPHW50/51 |
| Volcano | VFVO50, VFVO56 |
| Nankai Trough | VYSE50 |

Not every message delivered by a provider is converted into a caption. Messages may be excluded when they come from an unselected provider, use an unsupported format, do not meet the EEW warning criteria, are disabled by display filters, are damaged, duplicate an existing message, or have been superseded.

For weather warnings and advisories, CDI-Telopper does not replay the caption or audio when the post-filter page content is unchanged from the preceding revision in the same bulletin series. The message remains available in the received-message review with the status `No change to displayed information`.

By default, CDI-Telopper also suppresses captions and audio when every item remaining after weather filters is marked as continuing. New announcements, updates, and releases are still displayed. This behavior can be changed with the `Do not display telegrams containing only continuing items` checkbox. Audio for weather disaster-prevention bulletins (VPBS50/51) can be enabled and assigned separately from ordinary weather warning and advisory audio.

EEW audio has priority over all earthquake, tsunami, and weather audio. When an EEW arrives, pending weather audio is discarded and any currently playing affected sound is interrupted by the EEW sound. Earthquake, tsunami, and weather sounds received before the EEW sound finishes are not replayed later.

When redisplaying a telegram from the review window, choose one of three purposes. A live-information repeat badge shows only the telegram's issue time; past-information and training badges show both the purpose and issue time. Only telegrams actually received in production can use the live-information repeat mode; messages loaded from a history provider cannot be presented as live repeats.

## Reception providers

### P2PQuake API

P2PQuake is primarily used for EEW, earthquake, and tsunami information. Production and Sandbox connections are available.

The P2PQuake API is not an official API operated by JMA. Treat it as an external service carrying JMA-issued information, use official information alongside it, and review the provider's terms, rate limits, and secondary-use conditions.

### DMDATA.JP

You must provide your own subscription and API key. For EEW, select either a warning subscription (VXSE43) or forecast subscription (VXSE45), according to your contract. With a forecast subscription, CDI-Telopper displays VXSE45 messages that contain a warning, as well as their cancellations.

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

- CDI-Telopper 地震字幕・全ての音声 (earthquake captions and all audio)
- CDI-Telopper 緊急地震速報 (EEW)
- CDI-Telopper 津波字幕 (tsunami captions)
- CDI-Telopper 気象情報 (weather information)

Only **CDI-Telopper 地震字幕・全ての音声** should appear as an audio source in the OBS mixer. Disable audio control for the other three sources. OBS WebSocket synchronization can create missing sources, update the URLs that change at each application start, and migrate legacy source names.

Earthquake and tsunami maps and the always-on desktop overlay have been removed. Use the preview and the live/past telegram review window for on-PC confirmation.

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

The script restores dependencies, builds every project in the Release configuration, and runs the automated tests. The beta.42 source currently has 554 verified tests.

To create distributable packages, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.42
```

The folder package, single-file package, `version.json`, and `SHA256SUMS.txt` are written to `artifacts\release\2.0.0-beta.42\win-x64`. See [SOURCE_BUILD.md](SOURCE_BUILD.md) for details.

## License and attribution

- Application license: [LICENSE](LICENSE)
- Data sources and history retrieval: [docs/data-sources.md](docs/data-sources.md)
- Audio libraries and user-provided audio: [docs/assets-license.md](docs/assets-license.md)

User-selected audio files are not included in the repository or release packages.
