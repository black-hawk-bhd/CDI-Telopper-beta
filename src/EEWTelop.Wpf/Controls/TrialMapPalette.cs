using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Controls;

internal sealed record TrialMapPalette(Dictionary<string, string> Colors, bool ShowGrid = false)
{
    internal static readonly (string Key, string Label, string Color)[] Fields =
    [
        ("Background", "背景（海）", "#F2EFE8"), ("Land", "情報なしの陸地", "#D6D2C8"),
        ("Boundary", "地域境界", "#908A80"), ("Grid", "格子線", "#DED9CF"),
        ("Text", "文字・引出線", "#292B30"), ("Accent", "見出し・注記", "#8B431D"),
        ("Epicenter", "震央 ×", "#B32040"),
        ("0", "震度0", "#C6CBCF"), ("10", "震度1", "#B1C9D6"),
        ("20", "震度2", "#79B7B0"), ("30", "震度3", "#A7BF72"),
        ("40", "震度4", "#DCB65C"), ("45", "震度5弱", "#DD9255"),
        ("46", "震度5弱以上未入電", "#A790AE"), ("50", "震度5強", "#C76E50"),
        ("55", "震度6弱", "#B84E67"), ("60", "震度6強", "#8A486C"), ("70", "震度7", "#613C71"),
    ];
    internal static TrialMapPalette Default => new(Fields.ToDictionary(f => f.Key, f => f.Color));
    internal static TrialMapPalette Dark => Default with { Colors = Default.Colors.Concat(new Dictionary<string, string>
    { ["Background"] = "#252427", ["Land"] = "#454247", ["Boundary"] = "#89818B", ["Grid"] = "#38343B", ["Text"] = "#F4F0E9", ["Accent"] = "#E8B17A", ["Epicenter"] = "#FF7890" })
        .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value) };
    internal static bool IsValid(string? color) => color is { Length: 7 } && color[0] == '#' && uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
    internal static TrialMapPalette Normalize(TrialMapPalette? palette) => new(Fields.ToDictionary(f => f.Key,
        f => palette?.Colors is not null && palette.Colors.TryGetValue(f.Key, out var value) && IsValid(value) ? value : f.Color), palette?.ShowGrid ?? false);
    internal SolidColorBrush Brush(string key) => new((Color)ColorConverter.ConvertFromString(Colors[key]));
    internal SolidColorBrush Scale(JmaScale scale) => Brush(Colors.ContainsKey(((int)scale).ToString(CultureInfo.InvariantCulture)) ? ((int)scale).ToString(CultureInfo.InvariantCulture) : "0");
    internal static SolidColorBrush Contrast(Color color)
    {
        static double Linear(byte c) { double v = c / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return luminance > .179 ? Brushes.Black : Brushes.White;
    }
    internal static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QTelopper", "2.x-beta", "trial-map-colors.json");
    internal static TrialMapPalette Load(string path)
    {
        try { return File.Exists(path) ? Normalize(JsonSerializer.Deserialize<TrialMapPalette>(File.ReadAllText(path))) : Default; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return Default; }
    }
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(this)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
