# CDI-Telopper terms of use and disclaimer

Effective October 2, 2026. Updated November 15, 2026.

[日本語](TERMS.md) | English

## 1. Software license and third-party material

The CDI-Telopper source code is published under the MIT License. Include the copyright and permission notices with copies or substantial portions of the software. The MIT License governs use, modification and redistribution of the source code, including commercial use. [MIT License](LICENSE)

Received data, third-party APIs, libraries, assets and audio files supplied by users are subject to their respective providers' terms. Review and follow the applicable contracts, copyrights and conditions for reuse and redistribution. [Data sources](docs/data-sources.md) / [Third-party notices and audio](docs/assets-license.md)

## 2. Disaster information and rehearsal

CDI-Telopper is a development beta independently maintained by black-hawk-bhd. Reception, parsing, display, audio, cancellation, warning lifts and reconnection can be delayed, incomplete, incorrect or interrupted. Unsupported messages and filtered information may be omitted.

Always consult official information from the Japan Meteorological Agency or local authorities for evacuation and safety decisions. Test CDI-Telopper in your operating environment before use. Furigana is a reading aid with limited coverage. Verify official pronunciations before reading place names on air.

Keep training, test and past information clearly identified when presenting it. The option to hide the training banner is for private rehearsals. The internal training classification stays in effect when the banner is hidden.

## 3. Reception services and credentials

Depending on the provider, reception may require a valid subscription, credentials and permissions. OAuth authorization does not replace a subscription. Keep credentials private and observe provider connection and rate limits.

DMDATA.JP OAuth is available from beta.55. Users authorize CDI-Telopper on the DMDATA.JP website, and CDI-Telopper uses the granted scopes. Revoke unused authorization from CDI-Telopper or the DMDATA.JP dashboard. [OAuth setup guide](docs/dmdata-oauth.md) / [DMDATA.JP terms](https://dmdata.jp/terms/) / [JMA PULL feed usage notes](https://xml.kishou.go.jp/xmlpull.html)

## 4. External API integration terms

The following conditions apply to the External API integration feature and data obtained through it.

- Do not use the feature or its data for commercial purposes.
- Do not transmit the obtained data over the Internet. Manual video uploads and similar actions performed by the user are excepted.
- Do not duplicate or redistribute any part of the obtained data through relays or similar mechanisms.
- Clearly state that CDI-Telopper is used when connected software is used for video production, publication or streaming, including YouTube live streams. Attribution does not authorize commercial use or otherwise prohibited transmission or redistribution.
- Do not use the feature in public or commercial facilities, to control machine tools or medical devices, or in other systems affecting human life or physical safety.
- Follow the source API and data providers' terms, contracts and restrictions on reuse and redistribution. These conditions grant no additional permission from those providers.

The External API connects applications on the same PC. Keep External API and OBS credentials and connection URLs private.

## 5. Warranty and liability

CDI-Telopper and its External API integration are provided as is, without warranties of operation, compatibility, data accuracy, completeness, timeliness, continuity or fitness for a particular purpose. The developer disclaims liability for losses arising from use or inability to use the software to the extent permitted by law. Liability that cannot be excluded or limited under applicable law is unaffected.

## 6. Privacy, changes and contact

See the [Privacy Policy](PRIVACY_EN.md) for information handling. Changes to these terms are posted in this repository.

Report issues through GitHub or contact the maintainer by email. Keep personal information and credentials out of public reports. Do not attach passwords, API keys or tokens to email. [GitHub Issues](https://github.com/black-hawk-bhd/CDI-Telopper-beta/issues) / [cdi.telopper.contact@proton.me](mailto:cdi.telopper.contact@proton.me)
