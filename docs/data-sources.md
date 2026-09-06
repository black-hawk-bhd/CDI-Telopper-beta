# Data sources and attribution

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
