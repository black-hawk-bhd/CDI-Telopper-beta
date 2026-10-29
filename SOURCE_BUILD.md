# CDI-Telopper 2.0.0-beta.63 unified source build

Formal name: **Comprehensive Disaster Information Telopper**

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET 10 SDK 10.0.403 or a compatible newer .NET 10 SDK
- Internet access for the first NuGet restore
- PowerShell 5.1 or later

## Build and test

Run this command from the extracted source directory:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

The script restores packages, builds the complete solution in Release mode,
and runs all automated tests. Warnings are treated as errors.

The optional Chrome rendering regression test for the EEW vertical layout is
`node scripts/verify-eew-vertical.cjs`. It uses an isolated profile and mock
loopback page, without live reception or OBS. See [the layout guide](docs/eew-vertical-style.md).

The project-local `.dotnet-sdk` SDK takes precedence when present, so update it as
well as any system SDK. Both scripts accept `-DotnetPath C:\path\to\dotnet.exe`
to explicitly select a different installation. Self-contained builds require .NET 10.0.14 or newer and
reject older runtime packs, including an explicit downgrade through build flags.
Updating a system runtime does not update already-published self-contained apps;
rebuild both distribution formats and verify their bundled runtime versions.
The WPF application's default minimum runtime is also 10.0.14 for framework-dependent builds.
The runtime security guard's seven regression cases run as part of `verify.ps1`.
All 15 projects target `net10.0` or `net10.0-windows`; C# 12 is retained.
`global.json` selects the native Microsoft.Testing.Platform runner for .NET 10.
For a single project, use `dotnet test --project <test.csproj>` (and `--filter`
for MSTest filtering). The former positional project syntax is no longer used.

The JMA current-tsunami snapshot client only negotiates gzip/deflate, and rejects
remaining unsupported content encodings before opening the body. Brotli decoding
is disabled as defense in depth for CVE-2026-111497; the runtime update is still
required. Existing response size limits, timeouts and initial-sync behavior remain.

Test-case ZIP imports are limited to 64 MiB compressed, 1,024 entries (including
directories), 2 MiB of metadata reads, 32 MiB per expanded file, 128 MiB total
expanded data and a 1 MiB manifest. Actual streamed sizes are checked as well as
declared sizes. Existing exported payloads and additional images are retained.
The sum of compressed entry sizes is also capped at 64 MiB to prevent repeated
decompression of overlapping compressed ranges in malformed archives.

Provider reception merge buffers (including the Wolfx socket merger) wait rather
than drop accepted messages when full. Each stage retains at most 256 messages
and an estimated 128 MiB of UTF-16 payload/transport strings plus per-item overhead,
including the currently delivered message. These are per-stage queue budgets,
not a cap on total process memory. A single message exceeding the byte budget is
explicitly rejected. Cancellation/stop releases waiting producers; reconnect does
not discard an already received Wolfx message waiting for the consumer.
AXIS gap recovery streams responses under the existing 4 MiB feed / 24 MiB XML
limits, with a request-and-body deadline of at most 20 seconds (or a shorter
configured HttpClient timeout). Recovered telegrams also pass through a bounded
stream instead of being retained as a complete batch before delivery.

## Create distributable binaries

After verification succeeds, build the current unified beta release:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.63
```

Self-contained folder and single-file packages are written under:

- `artifacts\release\2.0.0-beta.63\win-x64`

Restore and publish both explicitly use `SelfContained=true`. Before packaging,
the script checks the SDK-generated runtime configuration for both formats and
records its core/desktop runtime versions in each package's `version.json`.
An existing release directory is never overwritten; use `-OutputLabel` for local
verification builds. These changes do not replace previously published releases.

The .NET 10 migration and compatibility notes are in [docs/dotnet10-migration.md](docs/dotnet10-migration.md).

The former stable/separated source-package helpers are not included in the
public repository and are not part of the current unified release procedure.

## Notes

- User-selected audio files are not included in this source archive.
- Runtime settings, logs, diagnostics, package caches, and previous build
  outputs are intentionally excluded.
- The internal project and namespace names remain `EEWTelop.*` for source-code
  continuity.
- For compatibility with QTelopper 2.x, CDI-Telopper stores settings, logs, and display state under
  `%LOCALAPPDATA%\QTelopper\2.x-beta` by default.
- The current build is the unified beta line. Historical stable and separated
  release materials are intentionally excluded from the public repository.
