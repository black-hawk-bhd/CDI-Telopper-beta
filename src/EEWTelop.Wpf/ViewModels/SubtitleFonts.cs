using System.Windows.Markup;
using System.Windows.Media;
using EEWTelop.Application.Configuration;
using FontFamily = System.Windows.Media.FontFamily;

namespace EEWTelop.Wpf.ViewModels;

public sealed record SubtitleFontOption(string Name, string DisplayName);

internal static class SubtitleFonts
{
    private const string FallbackFamilies = DisplaySettings.DefaultFontFamilyName + ", Yu Gothic UI, Meiryo";
    private static readonly Lazy<SubtitleFontOption[]> InstalledOptions = new(GetInstalledOptions);

    public static FontFamily DefaultFamily { get; } = new(FallbackFamilies);

    public static FontFamily CreateFamily(string name) =>
        string.Equals(name, DisplaySettings.DefaultFontFamilyName, StringComparison.OrdinalIgnoreCase)
            ? DefaultFamily : new FontFamily($"{name}, {FallbackFamilies}");

    public static IReadOnlyList<SubtitleFontOption> GetOptions(string selectedName)
    {
        SubtitleFontOption[] options = InstalledOptions.Value;
        return options.Any(option => string.Equals(option.Name, selectedName, StringComparison.Ordinal))
            ? options : [.. options, new SubtitleFontOption(selectedName, $"{selectedName}（保存済み）")];
    }

    private static SubtitleFontOption[] GetInstalledOptions()
    {
        XmlLanguage english = XmlLanguage.GetLanguage("en-US");
        XmlLanguage japanese = XmlLanguage.GetLanguage("ja-JP");
        var installed = Fonts.SystemFontFamilies
            .Select(font => new SubtitleFontOption(
                font.FamilyNames.TryGetValue(english, out string? name) ? name : font.Source,
                font.FamilyNames.TryGetValue(japanese, out string? label) ? label : name ?? font.Source))
            .Where(option => !string.Equals(option.Name, DisplaySettings.DefaultFontFamilyName,
                StringComparison.OrdinalIgnoreCase) && DisplaySettings.NormalizeFontFamilyName(option.Name) == option.Name)
            .DistinctBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCulture);
        return [new SubtitleFontOption(DisplaySettings.DefaultFontFamilyName, "BIZ UDPゴシック（既定）"), .. installed];
    }
}
