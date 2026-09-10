param([Parameter(Mandatory = $true)][string]$CsvPath)
$ErrorActionPreference = 'Stop'
# Input: Japan Post UTF-8 KEN_ALL CSV. Generate only prefecture/municipality readings.
$rows = Import-Csv -LiteralPath $CsvPath -Encoding utf8 -Header Code,OldZip,Zip,PrefKana,CityKana,TownKana,Pref,City,Town,F1,F2,F3,F4,F5,F6
$entries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
function Convert-ToHiragana([string]$value) {
    -join ($value.ToCharArray() | ForEach-Object {
        if ([int]$_ -ge 0x30A1 -and [int]$_ -le 0x30F6) { [char]([int]$_ - 0x60) } else { $_ }
    })
}
foreach ($row in $rows) {
    [void]$entries.Add("$($row.Pref)`t$($row.Pref)`t$(Convert-ToHiragana $row.PrefKana)")
    [void]$entries.Add("$($row.Pref)`t$($row.City)`t$(Convert-ToHiragana $row.CityKana)")
    # Split at the county suffix, not a later 郡 inside the municipality (上郡町).
    $countyName = [regex]::Match($row.City, '^.+?郡(.+[町村])$')
    $countyKana = [regex]::Match($row.CityKana, '^.+?グン(.+)$')
    if ($countyName.Success -and $countyKana.Success) {
        $shortName = $countyName.Groups[1].Value
        $shortKana = $countyKana.Groups[1].Value
        if ($shortName.Length -lt 2) { continue }
        [void]$entries.Add("$($row.Pref)`t$shortName`t$(Convert-ToHiragana $shortKana)")
    }
}
$cityWards = $rows | Where-Object { $_.City -match '^.+市.+区$' } |
    Select-Object Pref,City,CityKana -Unique | Group-Object { $_.Pref + ':' + ($_.City -replace '市.*$', '市') }
foreach ($group in $cityWards) {
    $first = $group.Group[0]
    $prefix = $first.CityKana
    foreach ($ward in $group.Group) {
        while (-not $ward.CityKana.StartsWith($prefix, [StringComparison]::Ordinal)) {
            $prefix = $prefix.Substring(0, $prefix.Length - 1)
        }
    }
    # Only retain a shared city reading when the municipal suffix is explicit.
    if ($prefix.EndsWith('シ', [StringComparison]::Ordinal)) {
        $city = $first.City -replace '市.*$', '市'
        [void]$entries.Add("$($first.Pref)`t$city`t$(Convert-ToHiragana $prefix)")
    }
}
$outputPath = Join-Path $PSScriptRoot '../src/EEWTelop.Wpf/Assets/place-readings.tsv'
$lines = @('# Japan Post UTF-8 KEN_ALL: prefecture, name, hiragana (tab-separated).') + @($entries | Sort-Object -CaseSensitive)
[IO.File]::WriteAllLines($outputPath, $lines, [Text.UTF8Encoding]::new($false))
Write-Output "Generated $($entries.Count) place readings."
