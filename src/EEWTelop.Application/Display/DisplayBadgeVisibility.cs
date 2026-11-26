using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

/// <summary>Resolve output-only visibility without changing original badges, source metadata or pages.</summary>
public static class DisplayBadgeVisibility
{
    public static bool HideInformationBadges(DisplayProgram program, DisplaySettings settings) =>
        settings.HideNormalInformationBadges || (!IsNormalProduction(program) && program.HideInformationBadges);

    public static bool UsesAlignedCaptionColumn(DisplayBlock block) =>
        block.StyleToken is DisplayStyleTokens.Intensity or DisplayStyleTokens.Tsunami;

    public static string? GetAlignedSubtitleBody(DisplayBlock source, DisplayBlock formatted) =>
        UsesAlignedCaptionColumn(source) && source.PrimaryText != formatted.PrimaryText
            ? source.PrimaryText : null;

    public static DisplayBlock FormatBlockForOutput(
        DisplayBlock block, DisplayProgram program, DisplaySettings settings)
    {
        if (!HideInformationBadges(program, settings) ||
            string.IsNullOrWhiteSpace(block.Badge)) return block;

        // Retain all type/severity text in the subtitle. Weather area headers stay above
        // their unchanged area rows; continuation rows with no badge are not relabelled.
        return block.WeatherHeading.Length > 0
            ? block with { WeatherHeading = PrefixLabel(block.Badge, block.WeatherHeading) }
            : block with { PrimaryText = PrefixLabel(block.Badge, block.PrimaryText) };
    }

    private static string PrefixLabel(string label, string text) =>
        text.StartsWith(label, StringComparison.Ordinal) ? text :
        text.Length == 0 ? label : $"{label}　{text}";

    // Replay and test programs can retain production source metadata and use their own presentation settings.
    private static bool IsNormalProduction(DisplayProgram program) =>
        program.SourceMode == SourceMode.Production && !program.IsReplayPresentation &&
        string.IsNullOrWhiteSpace(program.RehearsalLabel);
}
