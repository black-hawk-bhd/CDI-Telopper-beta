param(
    [ValidateSet('true', 'false')]
    [string]$AxisProviderEnabled = 'true',
    [ValidateSet('true', 'false')]
    [string]$DmdataProviderEnabled = 'true',
    [ValidateSet('true', 'false')]
    [string]$ExtendedFeaturesEnabled = 'true'
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $workspaceRoot '.dotnet8\dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { 'dotnet' }

if (Test-Path $localDotnet) {
    # App-host based test executables do not inherit the SDK location from the
    # dotnet command path. Point them at the bundled runtime explicitly so the
    # full test suite works on machines without a system-wide .NET install.
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
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

Push-Location $workspaceRoot
try {
    Invoke-Dotnet @('--version')
    $featureProperties = @(
        "-p:AxisProviderEnabled=$AxisProviderEnabled",
        "-p:DmdataProviderEnabled=$DmdataProviderEnabled",
        "-p:ExtendedFeaturesEnabled=$ExtendedFeaturesEnabled",
        '-p:NuGetAudit=false'
    )
    Invoke-Dotnet (@('restore', 'EEWTelop.sln', '--configfile', 'NuGet.Config') + $featureProperties)
    # The former interactive FlaUI suite is retained only in the local,
    # non-public archive and is not part of unattended verification.
    $automatedTestProjects = @(
        'tests/EEWTelop.Domain.Tests/EEWTelop.Domain.Tests.csproj',
        'tests/EEWTelop.Application.Tests/EEWTelop.Application.Tests.csproj',
        'tests/EEWTelop.Infrastructure.P2P.Tests/EEWTelop.Infrastructure.P2P.Tests.csproj',
        'tests/EEWTelop.Infrastructure.Dmdata.Tests/EEWTelop.Infrastructure.Dmdata.Tests.csproj',
        'tests/EEWTelop.Infrastructure.Axis.Tests/EEWTelop.Infrastructure.Axis.Tests.csproj',
        'tests/EEWTelop.Wpf.Tests/EEWTelop.Wpf.Tests.csproj'
    )
    foreach ($testProject in $automatedTestProjects) {
        Invoke-Dotnet (@('build', $testProject, '-c', 'Release', '--no-restore') + $featureProperties)
        Invoke-Dotnet (@('test', $testProject, '-c', 'Release', '--no-build', '--no-restore') + $featureProperties)
    }
}
finally {
    Pop-Location
}
