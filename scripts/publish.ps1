param(
    [string]$Version = '2.0.0-beta.77',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputLabel = '',
    [ValidateSet('true', 'false')]
    [string]$AxisProviderEnabled = 'true',
    [ValidateSet('true', 'false')]
    [string]$DmdataProviderEnabled = 'true',
    [ValidateSet('true', 'false')]
    [string]$ExtendedFeaturesEnabled = 'true',
    [ValidateSet('Auto', 'SingleFileAndLight', 'Both', 'SingleFileOnly')]
    [string]$PackageMode = 'Auto',
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
if ($PackageMode -eq 'Auto') {
    $legacyBeta = $versionMinor -eq 0 -and $versionPatch -eq 0 -and
        $versionSuffix -match '^-beta\.([0-9]+)(?:[.-]|$)' -and [int]$Matches[1] -lt 75
    $PackageMode = if ($versionMajor -eq 1 -or $legacyBeta) { 'Both' } else { 'SingleFileAndLight' }
}
$assemblyVersion = "$versionMajor.$versionMinor.$versionPatch.0"
$releaseLabel = if ([string]::IsNullOrWhiteSpace($OutputLabel)) { $Version } else { $OutputLabel }
if ($releaseLabel.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Output label contains invalid file-name characters: $releaseLabel"
}
$releaseRoot = Join-Path $workspaceRoot "artifacts\release\$releaseLabel\$RuntimeIdentifier"
$singleFilePackage = @{
    Kind = 'single-file'
    Zip = "CDI-Telopper-$Version-$RuntimeIdentifier-single-file.zip"
    SelfContained = $true
    SingleFile = $true
}
$packagesToBuild = @(switch ($PackageMode) {
    'Both' {
        @{ Kind = 'folder'; Zip = "CDI-Telopper-$Version-$RuntimeIdentifier-folder.zip"; SelfContained = $true; SingleFile = $false }
        $singleFilePackage
    }
    'SingleFileAndLight' {
        $singleFilePackage
        @{ Kind = 'framework-dependent'; Zip = "CDI-Telopper-$Version-$RuntimeIdentifier-framework-dependent.zip"; SelfContained = $false; SingleFile = $true }
    }
    'SingleFileOnly' { $singleFilePackage }
})
$msbuildProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:InformationalVersion=$Version",
    "-p:AxisProviderEnabled=$AxisProviderEnabled",
    "-p:DmdataProviderEnabled=$DmdataProviderEnabled",
    "-p:ExtendedFeaturesEnabled=$ExtendedFeaturesEnabled",
    '-p:NuGetAudit=false'
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
        [bool]$SelfContained,
        [string]$Commit,
        [string]$BuiltAtUtc,
        [object[]]$RuntimeFrameworks
    )

    $licenses = Join-Path $Directory 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    $readmeName = "README_CDI-Telopper_$Version.txt"
    $readmePath = Join-Path $workspaceRoot $readmeName
    if ($SelfContained -and (Test-Path -LiteralPath $readmePath)) {
        Copy-Item -LiteralPath $readmePath -Destination (Join-Path $Directory $readmeName)
    }
    else {
        if ($SelfContained) {
            $runtimeNote = 'Self-contained package (.NET Desktop Runtime included).'
        }
        else {
            $desktopVersion = ($RuntimeFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
            $runtimeNote = "Lightweight package. Install .NET 10 Desktop Runtime ($RuntimeIdentifier, $desktopVersion or a later .NET 10 patch).`r`nhttps://dotnet.microsoft.com/ja-jp/download/dotnet/10.0"
        }
        $template = Get-Content -LiteralPath (Join-Path $workspaceRoot 'docs\distribution-readme.txt') -Encoding UTF8 -Raw
        $template.Replace('{{version}}', $Version).Replace('{{runtime}}', $runtimeNote) |
            Set-Content -LiteralPath (Join-Path $Directory $readmeName) -Encoding UTF8
    }
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs\disaster-simulator.md') -Destination (Join-Path $Directory 'SIMULATOR_GUIDE.md')
    Copy-Item -LiteralPath (Join-Path $workspaceRoot 'docs\external-api-v1.md') -Destination (Join-Path $Directory 'EXTERNAL_API_V1.md')
    if ($versionMajor -eq 2) {
        Copy-Item -LiteralPath (Join-Path $workspaceRoot 'jmaxml_20261118_CDI-Telopper') `
            -Destination (Join-Path $Directory 'TrainingSamples') -Recurse
    }
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
        selfContained = $SelfContained
        runtimeFrameworks = $RuntimeFrameworks
        singleFile = $SingleFile
        extendedFeaturesEnabled = $ExtendedFeaturesEnabled -eq 'true'
        trimmed = $false
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'version.json') -Encoding UTF8
}

function Get-PublishedRuntimeFrameworks {
    param([string]$RuntimeConfigPath, [bool]$SelfContained)

    $options = (Get-Content -LiteralPath $RuntimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions
    if ($SelfContained) {
        $frameworks = @($options.includedFrameworks)
    }
    else {
        if ($options.includedFrameworks -or $options.rollForward -ne 'LatestPatch') {
            throw "The lightweight package must use the installed .NET 10 Desktop Runtime and roll forward to the latest patch: $RuntimeConfigPath"
        }
        $frameworks = @($options.frameworks)
    }
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $frameworkMatches = @($frameworks | Where-Object { $_.name -eq $name })
        $runtimeVersion = $null
        if ($frameworkMatches.Count -ne 1 -or -not [Version]::TryParse($frameworkMatches[0].version, [ref]$runtimeVersion) -or
            $runtimeVersion.Major -ne 10 -or $runtimeVersion -lt [Version]'10.0.14') {
            throw "Published $name must use .NET 10.0.14 or newer within .NET 10: $RuntimeConfigPath"
        }
    }
    return $frameworks
}

function Test-PublishedApplication {
    param([string]$Executable)

    $session = (Get-Process -Id $PID).SessionId
    $running = @(Get-Process -Name 'CDI-Telopper', 'QTelopper' -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $session)
    if ($running.Count -gt 0) {
        throw 'Exit the running CDI-Telopper from its tray menu before the startup smoke test.'
    }

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
    $process = $null
    try {
        $process = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $process.Refresh()
            if ($process.HasExited) {
                throw "Published application exited during smoke startup: $Executable (exit $($process.ExitCode))"
            }
            if ($process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -like 'CDI-Telopper *') {
                Write-Output "Startup verified: $Executable"
                return
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Published application did not open its main window: $Executable"
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $null = $process.CloseMainWindow()
                if (-not $process.WaitForExit(2000)) {
                    Stop-Process -Id $process.Id -Force
                    $process.WaitForExit()
                }
            }
            $process.Dispose()
        }
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
    $commit = Get-CommitId
    $builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $lastRestore = $null
    $bundleRuntimeConfig = Join-Path $workspaceRoot "src\EEWTelop.Wpf\bin\Release\$targetFramework\$RuntimeIdentifier\CDI-Telopper.runtimeconfig.json"
    foreach ($package in $packagesToBuild) {
        $output = Join-Path $releaseRoot $package.Kind
        $selfContained = $package.SelfContained.ToString().ToLowerInvariant()
        $deploymentProperties = $msbuildProperties + "-p:SelfContained=$selfContained"
        if (-not $package.SelfContained) {
            $deploymentProperties += '-p:RollForward=LatestPatch'
        }
        # Rid-specific restore must use the same mode as publish.
        if ($lastRestore -ne $selfContained) {
            Invoke-Dotnet (@(
                'restore', $project,
                '-r', $RuntimeIdentifier,
                '--configfile', (Join-Path $workspaceRoot 'NuGet.Config')) + $deploymentProperties)
            $lastRestore = $selfContained
        }

        $packagingProperties = @("-p:PublishSingleFile=$($package.SingleFile.ToString().ToLowerInvariant())")
        if ($package.SingleFile) {
            $packagingProperties += '-p:IncludeNativeLibrariesForSelfExtract=true'
        }
        Invoke-Dotnet (@(
            'publish', $project,
            '-c', 'Release',
            '-r', $RuntimeIdentifier,
            '--self-contained', $selfContained,
            '--no-restore',
            '-o', $output,
            '-p:PublishTrimmed=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false') + $deploymentProperties + $packagingProperties)

        # The SDK keeps the runtimeconfig used for the single-file bundle in bin/.
        $runtimeConfig = if ($package.SingleFile) { $bundleRuntimeConfig }
            else { Join-Path $output 'CDI-Telopper.runtimeconfig.json' }
        $frameworks = @(Get-PublishedRuntimeFrameworks -RuntimeConfigPath $runtimeConfig -SelfContained $package.SelfContained)
        Add-DistributionFiles -Directory $output -SingleFile $package.SingleFile -SelfContained $package.SelfContained `
            -Commit $commit -BuiltAtUtc $builtAtUtc -RuntimeFrameworks $frameworks
        if (-not $SkipSmokeTest) {
            Test-PublishedApplication -Executable (Join-Path $output 'CDI-Telopper.exe')
        }
    }
    if ($SkipSmokeTest) { Write-Warning 'Published application startup smoke test was skipped.' }

    $packages = @(foreach ($package in $packagesToBuild) {
        $output = Join-Path $releaseRoot $package.Kind
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $releaseRoot $package.Zip) -CompressionLevel Optimal
        [ordered]@{ kind = $package.Kind; path = $package.Zip; selfContained = $package.SelfContained }
    })

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
        $checksumTargets = @(foreach ($package in $packagesToBuild) {
            Join-Path $releaseRoot $package.Zip
            Join-Path (Join-Path $releaseRoot $package.Kind) 'CDI-Telopper.exe'
        }) + (Join-Path $releaseRoot 'version.json')
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
