# CDI-Telopper Privacy Policy

Effective October 2, 2026. Updated November 15, 2026.

[日本語](PRIVACY.md) | English

This policy describes how CDI-Telopper, a Windows application maintained by black-hawk-bhd, handles information. Available features depend on the installed version.

## 1. Purposes

CDI-Telopper processes information to receive and parse disaster bulletins, display captions, play audio, review reception history, maintain connections, save settings, display DMDATA.JP contract information on request, and support troubleshooting.

## 2. Local storage

- Settings include reception sources and endpoints, display and audio options, paths to user-selected files and credentials.
- Saving an External API connection key encrypts it with Windows DPAPI (`CurrentUser`) in `external-api-key.dpapi` in the app data directory. **接続キーの保存を解除** (Stop saving the connection key) removes the file. Settings profiles and diagnostic ZIPs omit this key.
- Received bulletins, events, history, state, caches and logs may be retained. The applicable settings and retention limits depend on the feature. Raw reception logging can include provider response content.
- From beta.58, EEW ordering checks save the original message and any available transport envelope to `raw-reception` with an `_ordering-review` suffix when a lower-numbered report is accepted as an exception or inconsistent ordering or cancellation information requires review. This can occur with general raw reception logging off. Existing retention limits apply, and CDI-Telopper does not upload these records automatically.
- Diagnostic ZIPs and settings backups are written to the destination selected by the user.

Data is stored in the application data directory or a location set by the user. Different files may have different retention and deletion behavior.

## 3. DMDATA.JP OAuth and contract details

Sign-in takes place in the browser on the DMDATA.JP website. CDI-Telopper handles the granted access and refresh tokens, public client ID, scopes and expiry. Tokens are encrypted for the current Windows user in `dmdata-oauth.json`. The authorization code, state and PKCE verifier are used during authorization and are not saved to disk.

Tokens are sent to official DMDATA.JP services for authorized reception, contract queries, refresh or revocation. CDI-Telopper does not send the tokens to the maintainer or include the OAuth session file in settings profiles or diagnostic ZIPs.

Contract details are retrieved only when the user requests them. The request requires `contract.list` through an API key or an additional OAuth authorization allowed by the client registration. The embedded client's permission for that scope has not been verified with a live account. Contract data may include plan, distribution category, status, daily or monthly limits, start time and additional connection capacity. Displayed amounts are API values, and actual billing can differ. Contract results are displayed temporarily. They are excluded from files, settings, logs, OBS and the External API. Changing authentication, applying a profile or starting another query clears the previous display.

**認可を解除** (Revoke authorization) requests server-side revocation and removes the stored session on success. A failed request can leave the saved session available for retry. Authorization can also be revoked from the DMDATA.JP dashboard. Removing local files or uninstalling the app does not necessarily revoke server-side authorization.

## 4. Connections and third-party services

Depending on the selected features, CDI-Telopper connects to DMDATA.JP, P2PQuake, AXIS, Wolfx, YDITS, JMA and the National Institute of Informatics. These services may receive the source IP address, request time, request content and any required credentials. This includes connections for reception, fallback, history retrieval and the tsunami snapshot requested when the External API is enabled.

Local OBS, browser monitor, External API and Simulator integrations exchange display or reception data according to the selected settings. CDI-Telopper does not automatically send usage statistics or diagnostic ZIPs to the maintainer. Browser cookies and personal information on external sites are handled under the respective site policies. [DMDATA.JP Privacy Policy](https://dmdata.jp/policy/) / [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)

## 5. Sharing and deletion

Logs, diagnostic ZIPs, settings and copied URLs can contain environment information, paths, reception content or credentials. Review files and remove unnecessary personal or secret information before sharing. Publishing a report, uploading a file or streaming with OBS sends information to the chosen destination.

Users can stop reception and saving and delete local logs, history and backups. Resetting settings does not necessarily remove every stored file or revoke third-party authorization. Back up data you need before deleting it.

## 6. Contact and updates

The maintainer receives email through Proton Mail. Sender addresses and message contents are used to respond and are deleted when no longer needed. Use GitHub Issues for public reports and keep credentials and personal information out of public posts and email attachments. Changes to this policy are published here. [cdi.telopper.contact@proton.me](mailto:cdi.telopper.contact@proton.me) / [Proton Privacy Policy](https://proton.me/legal/privacy) / [GitHub Issues](https://github.com/black-hawk-bhd/CDI-Telopper-beta/issues) / [Terms](TERMS_EN.md)
