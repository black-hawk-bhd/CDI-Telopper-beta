# CDI-Telopper 2.0.0-beta.31 unified source build

Formal name: **Comprehensive Disaster Information Telopper**

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET 8 SDK 8.0.423 or a compatible newer SDK
- Internet access for the first NuGet restore
- PowerShell 5.1 or later

## Build and test

Run this command from the extracted source directory:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
```

The script restores packages, builds the complete solution in Release mode,
and runs all automated tests. Warnings are treated as errors.

## Create distributable binaries

After verification succeeds, build the current unified beta release:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Version 2.0.0-beta.31 -OutputLabel 2.0.0-beta.31-full
```

Self-contained folder and single-file packages are written under:

- `artifacts\release\2.0.0-beta.31-full\win-x64`

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
