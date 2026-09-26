# Codex instructions for CDI-Telopper

## Scope

Work on the existing architecture; do not replace large subsystems unless explicitly requested.
Prefer small, reviewable changes with regression tests.

## Build discipline

- Target .NET 8 / C# 12 as configured by the repository.
- `TreatWarningsAsErrors=true` is intentional. Do not disable analyzers or blanket-suppress warnings merely to obtain a green build.
- Before finalizing changes, run the narrowest relevant test projects first, then the full solution tests when the environment supports `net8.0-windows`.

## Earthquake intensity semantics

The following values are intentionally different:

- `JmaScale.FiveLower` = an observed intensity 5-lower.
- `JmaScale.FiveLowerOrMore` = intensity 5-lower or greater is expected / indicated, but the detailed observed intensity has not been received (`未入電`).

Never normalize `FiveLowerOrMore` to ordinary `FiveLower`.
For display, the current text is `震度5弱以上 未入電`.

Accepted compatibility inputs in the JMA normalizer include JMA's unreported wording and legacy compatibility strings such as `!5-` / `5-?`, but canonical internal representation is `JmaScale.FiveLowerOrMore`.

## Observation-point marker

JMA station names can end in the full-width marker `＊`.
Only strip a trailing full-width `＊` for display composition. Do not mutate the domain/source name, station code, external API payload or signature data.
Do not strip ASCII `*` or a `＊` in the middle of a name.

## Provider normalization

- JMA XML and DMDATA raw XML share `JmaXmlEventNormalizer`.
- P2PQuake scale `46` represents the same unreported semantic state.
- Keep provider-specific parsing at provider boundaries and normalize into domain types before display logic.

## Rehearsal safety

Preserve the existing separation between simulator/rehearsal state and live production state. A visual switch that hides a training label must not change source metadata to live/production.

## Files most relevant to the latest fix

- `src/EEWTelop.Infrastructure.Dmdata/Normalization/JmaXmlEventNormalizer.cs`
- `src/EEWTelop.Application/Display/QuakePageComposer.cs`
- `src/EEWTelop.Wpf/ViewModels/OverlayViewModel.cs`
- `src/EEWTelop.Wpf/Obs/Assets/overlay.js`
- `tests/EEWTelop.Infrastructure.Dmdata.Tests/JmaXmlEventNormalizerTests.cs`
- `tests/EEWTelop.Infrastructure.P2P.Tests/P2pEventNormalizerTests.cs`
- `tests/EEWTelop.Application.Tests/QuakePageComposerTests.cs`
