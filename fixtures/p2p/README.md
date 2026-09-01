# P2P normalization fixtures

These payloads are fixed, synthetic/anonymized examples derived from the local
OpenAPI 2.3.0 schema and the retained HTML reference implementation. They never
contact the production API.

- `551-detail-scale.json`: normal earthquake information and same-name cities
  in different prefectures.
- `551-legacy-id.json`: compatibility `_id` alias.
- `552-tsunami.json`: mixed tsunami grades and nullable height fields.
- `556-eew.json`: top-level EEW issue, string/numeric kind codes, and scale 99.
- `invalid-*`: malformed JSON and a missing required object.
- `unknown-code.json`: safe handling of future provider codes.

The 10,000-point stress fixture is generated deterministically in the test to
avoid committing a multi-megabyte JSON file.
