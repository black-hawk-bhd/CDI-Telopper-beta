# CDI-Telopper 2.0.0-beta.61 unified source build

Formal name: **Comprehensive Disaster Information Telopper**

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET 8 SDK 8.0.426 or a compatible newer SDK
- Internet access for the first NuGet restore
- PowerShell 5.1 or later

## Build and test

Run this command from the extracted source directory:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

The script restores packages, builds the complete solution in Release mode,
and runs all automated tests. Warnings are treated as errors.

The project-local `.dotnet8` SDK takes precedence when present, so update it as
well as any system SDK. Self-contained builds require .NET 8.0.32 or newer and
reject older runtime packs, including an explicit downgrade through build flags.
Updating a system runtime does not update already-published self-contained apps;
rebuild both distribution formats and verify their bundled runtime versions.

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
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.61
```

Self-contained folder and single-file packages are written under:

- `artifacts\release\2.0.0-beta.61\win-x64`

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
