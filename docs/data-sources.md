# Data sources and attribution

## JMA XML (direct PULL feed)

An unauthenticated provider that retrieves XML directly from JMA's public feeds. CDI-Telopper is not an official JMA application.

- Supports the application's existing earthquake, tsunami, weather (including designated-river floods), volcano and Nankai Trough categories. **EEW is excluded.**
- Select “気象庁XML（60秒巡回・遅延あり）” per category and save. There is no automatic failover.
- Waits 60 seconds after each polling cycle. Weather uses `extra.xml`; earthquake, tsunami, volcano and Nankai Trough share `eqvol.xml`. Each required feed is checked once per cycle, regardless of how many categories select it. New telegram bodies require separate requests.
- Excludes `VPWW53`, `VPWW54` and `VPOA50`. Downloaded URLs are deduplicated within the retained history; failed downloads are retried on a later cycle.
- Publication can be delayed or interrupted. Pre-connection telegrams are not presented as new alerts, and long outages are not fully backfilled. “Connected” indicates successful polling, not a guarantee of new messages or captions.

[JMA feed documentation and usage notes](https://xml.kishou.go.jp/xmlpull.html)

### Manual history / replay

The received/past telegram window can fetch the official `eqvol_l.xml` and
`extra_l.xml` long feeds. JMA publishes several days of telegrams in these feeds,
updated hourly; this is not an unlimited historical archive or an EEW source.
Choose a supported category and 1–100 items, or a direct official XML URL under
`https://www.data.jma.go.jp/developer/xml/data/`. Only HTTPS URLs on that host and
path are accepted; redirects are disabled. Legacy live-excluded weather codes
are also excluded from feed selection. Normalization and compatibility semantics
are shared with the live JMA XML parser.

Reads are manual, serialized and bounded (8 MiB per response). Feed reads are
cached for one minute and telegram bodies in a bounded in-memory session cache,
with a 250 ms delay before each uncached telegram. Re-fetching a cached telegram
does not send another request. Partial/failed retrieval is reported as a failure,
not a successful complete history. Retrieval only fills the review list: it does
not emit captions or audio, update live API state, or enter live deduplication.
Replays remain `HistoryRehearsal` even when their visible mode label is hidden.
For older telegrams, use the NII archive or a locally saved XML file.

### NII archive retrieval

The manual NII archive source uses the [NII JMA XML database](https://agora.ex.nii.ac.jp/cps/weather/report/).
Each daily index or individual telegram request has a 60-second deadline covering
both response headers and the complete body. A timeout, interrupted connection,
or transient HTTP error (408, 429, 500, 502, 503, 504) is retried once after at
least two seconds. A longer `Retry-After` is respected; if it exceeds 30 seconds,
the request fails without an early retry. Requests remain paced at least one
second apart. Cancellation, permanent HTTP errors, oversized responses and invalid
telegram pages are not retried.

Only successfully retrieved XML is cached on disk. Failed retrieval keeps existing
cache entries for the next manual attempt and reports failure rather than a
complete result containing only part of the requested history. Timeout messages
identify whether the daily index or an individual telegram failed. Retrieval
remains separate from live state, captions and audio.

## Place-name ruby in telegram review

The telegram review window uses bundled prefecture and municipality readings
derived from Japan Post's UTF-8 KEN_ALL postcode dataset (retrieved 2026-09-07).
Source: https://www.post.japanpost.jp/service/search/zipcode/download/utf-zip.html
Format and terms: https://www.post.japanpost.jp/service/search/zipcode/download/utf-readme.html
Japan Post states that it does not assert copyright over the postcode data.

Only prefecture/municipality columns are retained; katakana is converted to
hiragana and county-free municipality aliases are added. Designated-city aliases
use the shared reading prefix of their wards when it ends in the city suffix.
No street addresses,
postcodes or network lookup are used at runtime. To regenerate the bundled TSV,
download/extract the official UTF-8 CSV and run
`scripts/update-place-readings.ps1 -CsvPath <path-to-utf_ken_all.csv>`.

Source ZIP SHA-256: `7d42728c4c9023e30668b1ef144fd5ffd831a417011c82d3e85ca3c35db43b15`.

Ruby decorates page text only in the received/past telegram review window.
Original page text, OBS, replay, speech and archived telegrams are unchanged.
Names with different readings are resolved against a preceding prefecture in
the same page; unresolved/unknown names remain undecorated. This is a municipal
dictionary, not comprehensive coverage of forecast regions, offshore locations,
foreign places, volcano names or historical municipality names.


CDI-Telopper can manually retrieve past earthquake and tsunami telegrams for its
history rehearsal feature from the following database:

- Database: 気象庁防災情報XMLデータベース
- Provider: 国立情報学研究所（National Institute of Informatics）
- URL: https://agora.ex.nii.ac.jp/cps/weather/report/
- License: Creative Commons Attribution 4.0 International (CC BY 4.0)
- License URL: https://creativecommons.org/licenses/by/4.0/

CDI-Telopper does not use this database for live disaster reception. Retrieval is
started only by an operator, is limited to earthquake and tsunami telegrams,
uses a one-second minimum interval between network requests, and caches fetched
XML telegrams locally to avoid repeated downloads.

The original disaster telegrams are produced by the Japan Meteorological
Agency. Operators should confirm important information against official sources.
