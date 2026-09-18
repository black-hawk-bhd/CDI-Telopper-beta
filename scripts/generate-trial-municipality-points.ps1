$ErrorActionPreference = 'Stop'
$base = 'https://www.jma.go.jp/bosai/common/const/'
$areas = Invoke-RestMethod ($base + 'area.json')
$positions = Invoke-RestMethod ($base + 'xy.json')
$prefectures = '北海道 青森県 岩手県 宮城県 秋田県 山形県 福島県 茨城県 栃木県 群馬県 埼玉県 千葉県 東京都 神奈川県 新潟県 富山県 石川県 福井県 山梨県 長野県 岐阜県 静岡県 愛知県 三重県 滋賀県 京都府 大阪府 兵庫県 奈良県 和歌山県 鳥取県 島根県 岡山県 広島県 山口県 徳島県 香川県 愛媛県 高知県 福岡県 佐賀県 長崎県 熊本県 大分県 宮崎県 鹿児島県 沖縄県'.Split(' ')
$points = foreach ($entry in $positions.class20s.PSObject.Properties) {
    $area = $areas.class20s.PSObject.Properties[$entry.Name].Value
    if (-not $area) { continue }
    $pref = [int]$entry.Name.Substring(0, 2)
    if ($pref -lt 1 -or $pref -gt 47) { throw 'Invalid prefecture' }
    [ordered]@{ code=$entry.Name; prefecture=$prefectures[$pref-1]; name=$area.name; latitude=$entry.Value[0]; longitude=$entry.Value[1] }
}
$document = [ordered]@{ source=@(($base+'area.json'), ($base+'xy.json')); retrievedAt=[DateTimeOffset]::UtcNow.ToString('o'); kind='municipality-representative'; points=@($points) }
$output = Join-Path $PSScriptRoot '../src/EEWTelop.Wpf/Assets/trial-municipality-points.json'
$document | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output "Generated $(@($points).Count) municipality representative points"
