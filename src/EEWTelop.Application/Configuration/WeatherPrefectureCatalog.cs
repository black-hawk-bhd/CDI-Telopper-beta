namespace EEWTelop.Application.Configuration;

public sealed record WeatherPrefectureOption(string Code, string Name);

public static class WeatherPrefectureCatalog
{
    public static IReadOnlyList<WeatherPrefectureOption> Options { get; } =
    [
        new(string.Empty, "全国（地域指定なし）"),
        new("01", "北海道"),
        new("02", "青森県"), new("03", "岩手県"), new("04", "宮城県"),
        new("05", "秋田県"), new("06", "山形県"), new("07", "福島県"),
        new("08", "茨城県"), new("09", "栃木県"), new("10", "群馬県"),
        new("11", "埼玉県"), new("12", "千葉県"), new("13", "東京都"),
        new("14", "神奈川県"), new("15", "新潟県"), new("16", "富山県"),
        new("17", "石川県"), new("18", "福井県"), new("19", "山梨県"),
        new("20", "長野県"), new("21", "岐阜県"), new("22", "静岡県"),
        new("23", "愛知県"), new("24", "三重県"), new("25", "滋賀県"),
        new("26", "京都府"), new("27", "大阪府"), new("28", "兵庫県"),
        new("29", "奈良県"), new("30", "和歌山県"), new("31", "鳥取県"),
        new("32", "島根県"), new("33", "岡山県"), new("34", "広島県"),
        new("35", "山口県"), new("36", "徳島県"), new("37", "香川県"),
        new("38", "愛媛県"), new("39", "高知県"), new("40", "福岡県"),
        new("41", "佐賀県"), new("42", "長崎県"), new("43", "熊本県"),
        new("44", "大分県"), new("45", "宮崎県"), new("46", "鹿児島県"),
        new("47", "沖縄県"),
    ];

    public static bool IsSupported(string? code) =>
        Options.Any(option => string.Equals(option.Code, code ?? string.Empty, StringComparison.Ordinal));

    public static WeatherPrefectureOption? Find(string? code) =>
        Options.FirstOrDefault(option =>
            string.Equals(option.Code, code ?? string.Empty, StringComparison.Ordinal));

    public static string[] NormalizeCodes(IEnumerable<string>? codes) =>
        (codes ?? [])
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Select(static code => code.Trim())
            .Where(IsSupported)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();

    public static string[] ResolveCodes(FilterSettings filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string[] codes = NormalizeCodes(filter.WeatherPrefectureCodes);
        if (codes.Length > 0)
        {
            return codes;
        }

        return !string.IsNullOrWhiteSpace(filter.WeatherPrefectureCode) &&
               IsSupported(filter.WeatherPrefectureCode)
            ? [filter.WeatherPrefectureCode]
            : [];
    }
}
