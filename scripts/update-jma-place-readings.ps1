param([Parameter(Mandatory = $true)][string]$ZipPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

function Read-XlsxPart([IO.Compression.ZipArchive]$workbook, [string]$path) {
    $entry = $workbook.GetEntry($path)
    if ($null -eq $entry) { throw "Missing workbook part: $path" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}

# These columns are from the JMA workbook 地震火山関連コード表.xlsx.
$tables = @(
    @{ Sheet = '24'; Table = 'AreaForecastLocalE'; Code = 'A'; Name = 'B'; Reading = 'C'; Length = 3; Municipality = 'D' }
    @{ Sheet = '24'; Table = 'PointSeismicIntensity'; Code = 'G'; Name = 'H'; Reading = 'I'; Length = 7; Municipality = 'D' }
    @{ Sheet = '31'; Table = 'AreaTsunami'; Code = 'A'; Name = 'B'; Reading = 'C'; Length = 3; Municipality = '' }
)

$zipFile = (Get-Item -LiteralPath $ZipPath).FullName
$zipHash = (Get-FileHash -LiteralPath $zipFile -Algorithm SHA256).Hash.ToLowerInvariant()
$entries = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$archive = [IO.Compression.ZipFile]::OpenRead($zipFile)
$buffer = [IO.MemoryStream]::new()
try {
    $entry = $archive.GetEntry('地震火山関連コード表.xlsx')
    if ($null -eq $entry) { throw 'The JMA earthquake and volcano code workbook is missing.' }
    $source = $entry.Open()
    try { $source.CopyTo($buffer) } finally { $source.Dispose() }
    $buffer.Position = 0
    $workbook = [IO.Compression.ZipArchive]::new($buffer)
    try {
        $sheets = (Read-XlsxPart $workbook 'xl/workbook.xml').workbook.sheets.sheet
        $parts = (Read-XlsxPart $workbook 'xl/_rels/workbook.xml.rels').Relationships.Relationship
        $shared = @((Read-XlsxPart $workbook 'xl/sharedStrings.xml').sst.si | ForEach-Object InnerText)
        foreach ($group in ($tables | Group-Object Sheet)) {
            $sheet = $sheets | Where-Object name -eq $group.Name
            $target = ($parts | Where-Object Id -eq $sheet.id).Target
            if (-not $target) { throw "Missing JMA worksheet $($group.Name)." }
            $path = if ($target.StartsWith('/')) { $target.TrimStart('/') } else { "xl/$target" }
            $rows = (Read-XlsxPart $workbook $path).worksheet.sheetData.row

            $header = $null
            $counts = @{}
            foreach ($row in $rows) {
                $cells = @{}
                foreach ($cell in $row.c) {
                    $column = $cell.r -replace '[0-9]+$', ''
                    $value = switch ($cell.t) {
                        's' { $shared[[int]$cell.v] }
                        'inlineStr' { $cell.is.InnerText }
                        default { $cell.v }
                    }
                    $cells[$column] = ([string]$value).Trim()
                }
                if ($row.r -eq '1') { $heading = $cells['A'] }
                if ($row.r -eq '3') {
                    $header = $cells
                    foreach ($table in $group.Group) {
                        if ($heading -notlike "*$($table.Table)*" -or
                            $header[$table.Code] -ne 'Code' -or $header[$table.Name] -ne 'Name' -or
                            $header[$table.Reading] -ne 'ふりがな') {
                            throw "Unexpected layout for $($table.Table) on worksheet $($group.Name)."
                        }
                    }
                }
                if ($null -eq $header -or [int]$row.r -lt 4) { continue }

                foreach ($table in $group.Group) {
                    $code = $cells[$table.Code]
                    $name = $cells[$table.Name]
                    $reading = $cells[$table.Reading]
                    if (-not $code -or -not $name -or -not $reading) { continue }
                    if ($code -notmatch "^[0-9]{$($table.Length)}$" -or $name -match '[\t\r\n]' -or $reading -match '[\t\r\n]') {
                        throw "Invalid code, name or reading for $($table.Table) on row $($row.r)."
                    }
                    $prefecture = ''
                    if ($table.Municipality) {
                        $municipality = $cells[$table.Municipality]
                        if ($municipality -notmatch '^[0-9]{7}$') {
                            throw "Missing parent municipality for $($table.Table) on row $($row.r)."
                        }
                        $prefecture = $municipality.Substring(0, 2)
                    }
                    if ($entries.Add(@($table.Table, $code, $prefecture, $name, $reading) -join "`t")) {
                        $counts[$table.Table]++
                    }
                }
            }
            foreach ($table in $group.Group) {
                if (-not $counts[$table.Table]) { throw "No readings found for $($table.Table)." }
                Write-Output "$($table.Table) $($counts[$table.Table]) readings."
            }
        }
    } finally { $workbook.Dispose() }
} finally {
    $buffer.Dispose()
    $archive.Dispose()
}

$sorted = [string[]]@($entries)
[Array]::Sort($sorted, [StringComparer]::Ordinal)
$lines = @(
    '# CDI-Telopper extracts code and reading columns from the JMA earthquake and volcano code tables.'
    '# Source https://xml.kishou.go.jp/tec_material.html'
    "# ZIP SHA-256 $zipHash"
    '# Tab-separated columns: code table, code, prefecture code (when available), name, reading.'
) + $sorted
$outputPath = Join-Path $PSScriptRoot '../src/EEWTelop.Wpf/Assets/jma-place-readings.tsv'
[IO.File]::WriteAllText($outputPath, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
