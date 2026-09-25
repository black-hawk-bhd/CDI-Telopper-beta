# CDI-Telopper 2.0.0-beta.49

## 修正内容

- Simulator経由の長周期地震動観測情報（VXSE62）で、案内だけになり階級・地域が表示されない問題を修正しました。
- `longPeriodIntensity` の最大階級・地域名・地域別階級を復元します。階級1～4だけを受理し、同一地域の重複は最大階級を採用。最大階級が欠ける場合は有効な地域階級で補完します。
- 通常のJMA XML正規化経路は変更しません。訓練区分・リハーサル表示切替・本番外部APIへの混入防止も維持しています。

## 注意事項

- **Simulator側も `longPeriodIntensity` を送信する必要があります。** 未送信の階級・地域情報はCDI側だけでは復元できません。元の0.3.1添付版はこの項目を送信しないため、対応したSimulatorをご利用ください。[連携ガイド](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/main/docs/disaster-simulator.md)
- 訓練バナー非表示は非公開リハーサル用です。実情報と区別できなくなるため、配信・録画・公開先を確認し、誤送出を防止してください。
- **フリガナは読みの正確性・網羅性を保証しません。配信前に公式情報で確認してください。** OBS字幕・音声には反映しません。
- 外部APIの非商用限定、送信・再配信制限、利用明記、安全に関わる用途の禁止、提供元規約の遵守・無保証は、同梱説明書と[外部API利用条件](https://github.com/black-hawk-bhd/CDI-Telopper-beta/blob/main/docs/external-api-v1.md)を確認してください。

## 配布・検証

Windows x64のフォルダ版・単一EXE版、version.json、SHA256SUMS.txtを添付します。Simulator本体は同梱していません。
自動テスト593件が通過しました。階級・地域の復元と字幕生成、重複・無効値の処理を確認済みです。実Simulator・OBSを使った目視確認は未実施です。
