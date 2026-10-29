param(
    [string]$Version = '2.0.0-beta.63',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputLabel = '',
    [ValidateSet('true', 'false')]
    [string]$AxisProviderEnabled = 'true',
    [ValidateSet('true', 'false')]
    [string]$DmdataProviderEnabled = 'true',
    [ValidateSet('true', 'false')]
    [string]$ExtendedFeaturesEnabled = 'true',
    [ValidateSet('Both', 'SingleFileOnly')]
    [string]$PackageMode = 'Both',
    [switch]$SkipSmokeTest,
    [switch]$SkipSha256,
    [string]$DotnetPath = ''
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$localDotnet = Join-Path $workspaceRoot '.dotnet-sdk\dotnet.exe'
$dotnet = if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) { $DotnetPath }
    elseif (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$dotnetCommand = Get-Command $dotnet -ErrorAction Stop
$dotnet = $dotnetCommand.Source
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$project = Join-Path $workspaceRoot 'src\EEWTelop.Wpf\EEWTelop.Wpf.csproj'
$targetFramework = [string](([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.TargetFramework |
    Where-Object { $_ } | Select-Object -First 1)
$versionMatch = [regex]::Match(
    $Version,
    '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?<suffix>-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')
if (-not $versionMatch.Success) {
    throw "Version must be SemVer (for example 1.3.3 or 2.0.0-beta.1): $Version"
}
$versionMajor = [int]$versionMatch.Groups['major'].Value
$versionMinor = [int]$versionMatch.Groups['minor'].Value
$versionPatch = [int]$versionMatch.Groups['patch'].Value
$versionSuffix = $versionMatch.Groups['suffix'].Value
if ($versionMajor -ne 1 -and -not ($versionMajor -eq 2 -and $versionSuffix)) {
    throw "Stable releases must remain 1.x; 2.x builds require a prerelease suffix: $Version"
}
$assemblyVersion = "$versionMajor.$versionMinor.$versionPatch.0"
$releaseLabel = if ([string]::IsNullOrWhiteSpace($OutputLabel)) { $Version } else { $OutputLabel }
if ($releaseLabel.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Output label contains invalid file-name characters: $releaseLabel"
}
$releaseRoot = Join-Path $workspaceRoot "artifacts\release\$releaseLabel\$RuntimeIdentifier"
$folderOutput = Join-Path $releaseRoot 'folder'
$singleOutput = Join-Path $releaseRoot 'single-file'
$msbuildProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$Version",
    "-p:AxisProviderEnabled=$AxisProviderEnabled",
    "-p:DmdataProviderEnabled=$DmdataProviderEnabled",
    "-p:ExtendedFeaturesEnabled=$ExtendedFeaturesEnabled",
    '-p:NuGetAudit=false'
    # RID alone does not imply self-contained. Restore the same
    # deployment graph that both --no-restore publish commands consume.
    '-p:SelfContained=true'
)
$enabledProviders = @('P2PQuake', 'Wolfx')
if ($DmdataProviderEnabled -eq 'true') { $enabledProviders += 'DMDATA.JP' }
if ($AxisProviderEnabled -eq 'true') { $enabledProviders += 'AXIS' }
$dataEnvironmentVariable = if ($versionMajor -ge 2) {
    'QTELOPPER_V2_BETA_DATA_DIRECTORY'
} else {
    'QTELOPPER_V1_DATA_DIRECTORY'
}

if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release directory already exists: $releaseRoot"
}

$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $workspaceRoot '.nuget\packages'
$env:APPDATA = Join-Path $workspaceRoot '.appdata'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-CommitId {
    $gitDirectory = Join-Path $workspaceRoot '.git'
    if (-not (Test-Path -LiteralPath $gitDirectory)) {
        return 'unversioned-source-folder'
    }

    try {
        $commit = (& git -C $workspaceRoot rev-parse HEAD 2>$null).Trim()
        if ($LASTEXITCODE -eq 0 -and $commit) { return $commit }
    }
    catch {
    }

    return 'unknown'
}

function Add-DistributionFiles {
    param(
        [string]$Directory,
        [bool]$SingleFile,
        [string]$Commit,
        [string]$BuiltAtUtc,
        [object[]]$RuntimeFrameworks
    )

    $licenses = Join-Path $Directory 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    $readmeName = "README_CDI-Telopper_$Version.txt"
    $readmePath = Join-Path $workspaceRoot $readmeName
    if (-not (Test-Path -LiteralPath $readmePath)) {
        $readmePath = Get-ChildItem -LiteralPath $workspaceRoot -Filter 'README_CDI-Telopper_2.0.0-beta.*.txt' -File |
            Sort-Object LastWriteTimeUtc | Select-Object -Last 1 -ExpandProperty FullName
    }
    if ([string]::IsNullOrWhiteSpace($readmePath) -or -not (Test-Path -LiteralPath $readmePath)) {
        # 名称移行直後の旧READMEも入力として利用し、配布物内では新名称へ変換する。
        $readmePath = Get-ChildItem -LiteralPath $workspaceRoot -Filter 'README_QTelopper_2.0.0-beta.*.txt' -File |
            Sort-Object LastWriteTimeUtc | Select-Object -Last 1 -ExpandProperty FullName
    }
    if ([IO.Path]::GetFileName($readmePath).StartsWith('README_QTelopper_', [StringComparison]::OrdinalIgnoreCase)) {
        (Get-Content -LiteralPath $readmePath -Raw).Replace('QTelopper', 'CDI-Telopper') |
            Set-Content -LiteralPath (Join-Path $Directory $readmeName) -Encoding UTF8
    }
    else {
        Copy-Item -LiteralPath $readmePath -Destination (Join-Path $Directory $readmeName)
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs\disaster-simulator.md') -Destination (Join-Path $Directory 'SIMULATOR_GUIDE.md')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs\external-api-v1.md') -Destination (Join-Path $Directory 'EXTERNAL_API_V1.md')
    foreach ($policyFile in @('TERMS.md', 'PRIVACY.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $workspaceRoot $policyFile) -Destination (Join-Path $Directory $policyFile)
    }
    $guidesDirectory = Join-Path $Directory 'docs'
    New-Item -ItemType Directory -Path $guidesDirectory | Out-Null
    foreach ($guideFile in @('data-sources.md', 'dmdata-oauth.md', 'eew-vertical-style.md', 'dotnet10-migration.md')) {
        $guidePath = Join-Path $workspaceRoot "docs\$guideFile"
        if (Test-Path -LiteralPath $guidePath) {
            Copy-Item -LiteralPath $guidePath -Destination (Join-Path $guidesDirectory $guideFile)
        }
    }
    $manualName = "MANUAL_CDI-Telopper_$Version.txt"
    $manualPath = Join-Path $workspaceRoot $manualName
    if (Test-Path -LiteralPath $manualPath) {
        Copy-Item -LiteralPath $manualPath -Destination (Join-Path $Directory $manualName)
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs\assets-license.md') -Destination (Join-Path $licenses 'audio-assets.md')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs\data-sources.md') -Destination (Join-Path $licenses 'data-sources.md')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'LICENSE') -Destination (Join-Path $licenses 'CDI-TELOPPER-LICENSE.txt')
    $dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
    $dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
    if (-not (Test-Path -LiteralPath $dotnetLicense) -or -not (Test-Path -LiteralPath $dotnetNotices)) {
        throw "The .NET license files were not found next to the selected dotnet executable: $dotnetRoot"
    }
    Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $licenses 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $licenses 'DOTNET-THIRD-PARTY-NOTICES.txt')

    $metadata = [ordered]@{
        application = 'CDI-Telopper'
        formalName = 'Comprehensive Disaster Information Telopper'
        providers = $enabledProviders
        version = $Version
        commit = $Commit
        builtAtUtc = $BuiltAtUtc
        runtimeIdentifier = $RuntimeIdentifier
        targetFramework = $targetFramework
        selfContained = $true
        runtimeFrameworks = $RuntimeFrameworks
        singleFile = $SingleFile
        extendedFeaturesEnabled = $ExtendedFeaturesEnabled -eq 'true'
        trimmed = $false
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'version.json') -Encoding UTF8
}

function Get-PublishedRuntimeFrameworks {
    param([string]$RuntimeConfigPath)

    $frameworks = @((Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks)
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $frameworkMatches = @($frameworks | Where-Object { $_.name -eq $name })
        $runtimeVersion = $null
        if ($frameworkMatches.Count -ne 1 -or -not [Version]::TryParse($frameworkMatches[0].version, [ref]$runtimeVersion) -or
            $runtimeVersion.Major -ne 10 -or $runtimeVersion -lt [Version]'10.0.14') {
            throw "Published $name must be self-contained .NET 10.0.14 or newer: $RuntimeConfigPath"
        }
    }
    return $frameworks
}

function Test-PublishedApplication {
    param([string]$Executable)

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $smokeDirectory = Join-Path $temporaryRoot ("CDI-Telopper-publish-smoke-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $smokeDirectory | Out-Null
    $previousDataDirectory = [Environment]::GetEnvironmentVariable(
        $dataEnvironmentVariable,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $dataEnvironmentVariable,
        $smokeDirectory,
        [EnvironmentVariableTarget]::Process)
    try {
        $process = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 3
        if ($process.HasExited) {
            throw "Published application exited during smoke startup: $Executable (exit $($process.ExitCode))"
        }

        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit()
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $dataEnvironmentVariable,
            $previousDataDirectory,
            [EnvironmentVariableTarget]::Process)
        $resolvedSmoke = [IO.Path]::GetFullPath($smokeDirectory)
        if ($resolvedSmoke.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedSmoke)) {
            Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force
        }
    }
}

Push-Location $workspaceRoot
try {
    Invoke-Dotnet (@(
        'restore', $project,
        '-r', $RuntimeIdentifier,
        '--configfile', (Join-Path $workspaceRoot 'NuGet.Config')) + $msbuildProperties)

    if ($PackageMode -eq 'Both') {
        Invoke-Dotnet (@(
            'publish', $project,
            '-c', 'Release',
            '-r', $RuntimeIdentifier,
            '--self-contained', 'true',
            '--no-restore',
            '-o', $folderOutput,
            '-p:PublishSingleFile=false',
            '-p:PublishTrimmed=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false') + $msbuildProperties)
        $folderFrameworks = @(Get-PublishedRuntimeFrameworks (Join-Path $folderOutput 'CDI-Telopper.runtimeconfig.json'))
    }

    Invoke-Dotnet (@(
        'publish', $project,
        '-c', 'Release',
        '-r', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '-o', $singleOutput,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false') + $msbuildProperties)
    # The SDK keeps the runtimeconfig used for the single-file bundle in bin/.
    $singleRuntimeConfig = Join-Path $workspaceRoot "src\EEWTelop.Wpf\bin\Release\$targetFramework\$RuntimeIdentifier\CDI-Telopper.runtimeconfig.json"
    $singleFrameworks = @(Get-PublishedRuntimeFrameworks $singleRuntimeConfig)

    $commit = Get-CommitId
    $builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    if ($PackageMode -eq 'Both') {
        Add-DistributionFiles -Directory $folderOutput -SingleFile $false -Commit $commit -BuiltAtUtc $builtAtUtc -RuntimeFrameworks $folderFrameworks
    }
    Add-DistributionFiles -Directory $singleOutput -SingleFile $true -Commit $commit -BuiltAtUtc $builtAtUtc -RuntimeFrameworks $singleFrameworks

    if (-not $SkipSmokeTest) {
        if ($PackageMode -eq 'Both') {
            Test-PublishedApplication -Executable (Join-Path $folderOutput 'CDI-Telopper.exe')
        }
        Test-PublishedApplication -Executable (Join-Path $singleOutput 'CDI-Telopper.exe')
    }
    else {
        Write-Warning 'Published application startup smoke test was skipped.'
    }

    $folderZip = Join-Path $releaseRoot "CDI-Telopper-$Version-$RuntimeIdentifier-folder.zip"
    $singleZip = Join-Path $releaseRoot "CDI-Telopper-$Version-$RuntimeIdentifier-single-file.zip"
    if ($PackageMode -eq 'Both') {
        Compress-Archive -Path (Join-Path $folderOutput '*') -DestinationPath $folderZip -CompressionLevel Optimal
    }
    Compress-Archive -Path (Join-Path $singleOutput '*') -DestinationPath $singleZip -CompressionLevel Optimal

    $packages = @()
    if ($PackageMode -eq 'Both') {
        $packages += [ordered]@{ kind = 'folder'; path = [IO.Path]::GetFileName($folderZip) }
    }
    $packages += [ordered]@{ kind = 'single-file'; path = [IO.Path]::GetFileName($singleZip) }

    $topVersion = [ordered]@{
        application = 'CDI-Telopper'
        formalName = 'Comprehensive Disaster Information Telopper'
        providers = $enabledProviders
        version = $Version
        commit = $commit
        builtAtUtc = $builtAtUtc
        runtimeIdentifier = $RuntimeIdentifier
        extendedFeaturesEnabled = $ExtendedFeaturesEnabled -eq 'true'
        packages = $packages
    }
    $topVersion | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseRoot 'version.json') -Encoding UTF8

    if (-not $SkipSha256) {
        $checksumTargets = @(
            $singleZip,
            (Join-Path $singleOutput 'CDI-Telopper.exe'),
            (Join-Path $releaseRoot 'version.json')
        )
        if ($PackageMode -eq 'Both') {
            $checksumTargets = @(
                $folderZip,
                (Join-Path $folderOutput 'CDI-Telopper.exe')
            ) + $checksumTargets
        }
        $checksumLines = foreach ($target in $checksumTargets) {
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            $relative = $target.Substring($releaseRoot.Length).TrimStart('\').Replace('\', '/')
            "$hash  $relative"
        }
        $checksumLines | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ASCII
    }
    Write-Output $releaseRoot
}
finally {
    Pop-Location
}
