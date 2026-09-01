# Data sources and attribution

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
