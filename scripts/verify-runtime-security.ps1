param([string]$DotnetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command $DotnetPath -ErrorAction Stop).Source
$probe = Join-Path $PSScriptRoot '..\tests\build\RuntimeSecurityBaseline.proj'
$cases = @(
    @{ Name = 'explicit old runtime'; Requested = '10.0.13'; Core = '10.0.13'; Desktop = '10.0.13'; SelfContained = 'true'; Accept = $false },
    @{ Name = 'old resolved core pack'; Requested = '10.0.14'; Core = '10.0.13'; Desktop = '10.0.14'; SelfContained = 'true'; Accept = $false },
    @{ Name = 'old resolved desktop pack'; Requested = '10.0.14'; Core = '10.0.14'; Desktop = '10.0.13'; SelfContained = 'true'; Accept = $false },
    @{ Name = 'implicit old runtime'; Requested = ''; Core = '10.0.13'; Desktop = '10.0.13'; SelfContained = 'true'; Accept = $false },
    @{ Name = 'legacy runtime pack'; Requested = '10.0.14'; Core = '8.0.33'; Desktop = '10.0.14'; SelfContained = 'true'; Accept = $false },
    @{ Name = 'patched self-contained runtime'; Requested = '10.0.14'; Core = '10.0.14'; Desktop = '10.0.14'; SelfContained = 'true'; Accept = $true },
    @{ Name = 'framework-dependent compatibility'; Requested = '10.0.13'; Core = '10.0.13'; Desktop = '10.0.13'; SelfContained = 'false'; Accept = $true }
)

foreach ($case in $cases) {
    $output = & $dotnet msbuild $probe -nologo -verbosity:minimal -target:ResolveFrameworkReferences `
        "-p:SelfContained=$($case.SelfContained)" "-p:RequestedRuntimeVersion=$($case.Requested)" `
        "-p:CoreRuntimeVersion=$($case.Core)" "-p:DesktopRuntimeVersion=$($case.Desktop)" 2>&1
    $accepted = $LASTEXITCODE -eq 0
    if ($accepted -ne $case.Accept -or (-not $accepted -and ($output -join "`n") -notmatch '(security baseline 10\.0\.14|runtime 10\.0\.14 or newer)')) {
        throw "Runtime security guard failed: $($case.Name)`n$($output -join "`n")"
    }
    Write-Output "PASS: $($case.Name)"
}
