using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

/// <summary>
/// 緊急地震速報の対象地域を府県予報区単位へ正規化し、広域発表時だけ地方名へ集約します。
/// </summary>
public static class EewAreaLabelFormatter
{
    private const int RegionalAggregationThreshold = 8;
    private const int NormalRegionalMemberThreshold = 3;
    private const int WideAreaRegionThreshold = 4;

    private static readonly RegionDefinition[] Regions =
    [
        new("北海道", ["北海道道央", "北海道道南", "北海道道北", "北海道道東"]),
        new("東北", ["青森", "岩手", "宮城", "秋田", "山形", "福島"]),
        new("関東", ["茨城", "栃木", "群馬", "埼玉", "千葉", "東京", "神奈川"]),
        new("伊豆諸島", ["伊豆大島", "新島", "神津島", "三宅島", "八丈島"]),
        new("小笠原", ["小笠原"]),
        new("新潟", ["新潟"]),
        new("北陸", ["富山", "石川", "福井"]),
        new("甲信", ["山梨", "長野"]),
        new("東海", ["岐阜", "静岡", "愛知", "三重"]),
        new("近畿", ["滋賀", "京都", "大阪", "兵庫", "奈良", "和歌山"]),
        new("中国", ["鳥取", "島根", "岡山", "広島", "山口"]),
        new("四国", ["徳島", "香川", "愛媛", "高知"]),
        new("九州", ["福岡", "佐賀", "長崎", "熊本", "大分", "宮崎", "鹿児島"]),
        new("奄美", ["奄美群島"]),
        new("沖縄", ["沖縄本島", "大東島", "宮古島", "八重山"]),
    ];

    private static readonly Dictionary<string, RegionDefinition> RegionByDistrict =
        Regions
            .SelectMany(static region => region.Districts.Select(district => (district, region)))
            .ToDictionary(static item => item.district, static item => item.region, StringComparer.Ordinal);

    private static readonly string[] StandardPrefectures =
    [
        "青森", "岩手", "宮城", "秋田", "山形", "福島",
        "茨城", "栃木", "群馬", "埼玉", "千葉", "東京", "神奈川",
        "新潟", "富山", "石川", "福井", "山梨", "長野",
        "岐阜", "静岡", "愛知", "三重",
        "滋賀", "京都", "大阪", "兵庫", "奈良", "和歌山",
        "鳥取", "島根", "岡山", "広島", "山口",
        "徳島", "香川", "愛媛", "高知",
        "福岡", "佐賀", "長崎", "熊本", "大分", "宮崎", "鹿児島",
    ];

    /// <summary>
    /// 表示用の地域名を受信順で返します。
    /// </summary>
    public static IReadOnlyList<string> Format(IReadOnlyList<EewArea> areas)
    {
        ArgumentNullException.ThrowIfNull(areas);

        ForecastDistrict[] districts = areas
            .Select(ResolveForecastDistrict)
            .Where(static district => district is not null)
            .Select(static district => district!)
            .DistinctBy(static district => district.Key, StringComparer.Ordinal)
            .ToArray();

        if (districts.Length < RegionalAggregationThreshold)
        {
            return districts.Select(static district => district.Label).ToArray();
        }

        Dictionary<string, ForecastDistrict[]> districtsByRegion = districts
            .Where(static district => district.Region is not null)
            .GroupBy(static district => district.Region!.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);

        bool useWideAreaRule = districtsByRegion.Count >= WideAreaRegionThreshold;
        HashSet<string> aggregateRegions = districtsByRegion
            .Where(pair => ShouldAggregate(pair.Value.Length, pair.Value[0].Region!, useWideAreaRule))
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        var result = new List<string>(districts.Length);
        var emittedRegions = new HashSet<string>(StringComparer.Ordinal);
        foreach (ForecastDistrict district in districts)
        {
            RegionDefinition? region = district.Region;
            if (region is null || !aggregateRegions.Contains(region.Name))
            {
                result.Add(district.Label);
                continue;
            }

            if (emittedRegions.Add(region.Name))
            {
                result.Add(region.Name);
            }
        }

        return result;
    }

    private static bool ShouldAggregate(
        int announcedDistrictCount,
        RegionDefinition region,
        bool useWideAreaRule)
    {
        if (useWideAreaRule)
        {
            return announcedDistrictCount >= 2;
        }

        return announcedDistrictCount >= NormalRegionalMemberThreshold ||
               (region.Districts.Count <= NormalRegionalMemberThreshold &&
                announcedDistrictCount == region.Districts.Count);
    }

    private static ForecastDistrict? ResolveForecastDistrict(EewArea area)
    {
        string prefecture = Compact(area.Prefecture);
        string areaName = Compact(area.Name);
        string? label = ResolveSpecialDistrict(prefecture, areaName) ??
                        ResolveStandardPrefecture(prefecture) ??
                        ResolveStandardPrefecture(areaName);

        if (label is not null)
        {
            RegionByDistrict.TryGetValue(label, out RegionDefinition? region);
            return new ForecastDistrict(label, label, region);
        }

        string fallbackLabel = string.IsNullOrWhiteSpace(area.Prefecture)
            ? area.Name.Trim()
            : area.Prefecture.Trim();
        if (fallbackLabel.Length == 0)
        {
            return null;
        }

        return new ForecastDistrict(Compact(fallbackLabel), fallbackLabel, null);
    }

    private static string? ResolveSpecialDistrict(string prefecture, string areaName)
    {
        string source = string.Concat(prefecture, "\n", areaName);

        if (ContainsAny(source, "北海道道央", "北海道央", "石狩", "後志", "空知"))
        {
            return "北海道道央";
        }

        if (ContainsAny(source, "北海道道南", "渡島", "檜山", "胆振", "日高"))
        {
            return "北海道道南";
        }

        if (ContainsAny(source, "北海道道北", "上川", "留萌", "宗谷"))
        {
            return "北海道道北";
        }

        if (ContainsAny(source, "北海道道東", "網走", "北見", "紋別", "十勝", "釧路", "根室"))
        {
            return "北海道道東";
        }

        if (source.Contains("伊豆大島", StringComparison.Ordinal))
        {
            return "伊豆大島";
        }

        if (source.Contains("新島", StringComparison.Ordinal))
        {
            return "新島";
        }

        if (source.Contains("神津島", StringComparison.Ordinal))
        {
            return "神津島";
        }

        if (source.Contains("三宅島", StringComparison.Ordinal))
        {
            return "三宅島";
        }

        if (source.Contains("八丈島", StringComparison.Ordinal))
        {
            return "八丈島";
        }

        if (source.Contains("小笠原", StringComparison.Ordinal))
        {
            return "小笠原";
        }

        if (source.Contains("奄美", StringComparison.Ordinal))
        {
            return "奄美群島";
        }

        if (ContainsAny(source, "沖縄本島", "沖縄県本島", "久米島"))
        {
            return "沖縄本島";
        }

        if (source.Contains("大東島", StringComparison.Ordinal))
        {
            return "大東島";
        }

        if (source.Contains("宮古島", StringComparison.Ordinal))
        {
            return "宮古島";
        }

        if (ContainsAny(source, "八重山", "石垣島", "与那国島", "西表島"))
        {
            return "八重山";
        }

        return prefecture switch
        {
            "伊豆諸島" => "伊豆諸島",
            "奄美群島" => "奄美群島",
            "沖縄本島" => "沖縄本島",
            "大東島" => "大東島",
            "宮古島" => "宮古島",
            "八重山" => "八重山",
            _ => null,
        };
    }

    private static string? ResolveStandardPrefecture(string value)
    {
        foreach (string prefecture in StandardPrefectures)
        {
            if (value.Equals(prefecture, StringComparison.Ordinal) ||
                value.StartsWith(string.Concat(prefecture, "県"), StringComparison.Ordinal) ||
                value.StartsWith(string.Concat(prefecture, "府"), StringComparison.Ordinal) ||
                value.StartsWith(string.Concat(prefecture, "都"), StringComparison.Ordinal))
            {
                return prefecture;
            }
        }

        return null;
    }

    private static string Compact(string value) =>
        (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("　", string.Empty, StringComparison.Ordinal);

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.Ordinal));

    private sealed record RegionDefinition(string Name, IReadOnlyList<string> Districts);

    private sealed record ForecastDistrict(string Key, string Label, RegionDefinition? Region);
}
