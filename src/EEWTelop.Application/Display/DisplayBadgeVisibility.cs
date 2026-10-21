using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

/// <summary>Resolve output-only visibility without changing original badges, source metadata or pages.</summary>
public static class DisplayBadgeVisibility
{
    public static bool HideModeBanner(DisplayProgram program, DisplaySettings settings) =>
        IsNormalProduction(program) ? settings.HideNormalModeBanner : program.HideReplayModeBanner;

    public static bool HideInformationBadges(DisplayProgram program, DisplaySettings settings) =>
        IsNormalProduction(program) ? settings.HideNormalInformationBadges : program.HideInformationBadges;

    public static DisplayBlock FormatBlockForOutput(
        DisplayBlock block, DisplayProgram program, DisplaySettings settings)
    {
        if (!IsNormalProduction(program) || !settings.HideNormalInformationBadges ||
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

    // A production-source EEW test can still carry a training label. Never use normal-mode
    // choices for that case, or for automatic live rotation whose source remains Production.
    private static bool IsNormalProduction(DisplayProgram program) =>
        program.SourceMode == SourceMode.Production && !program.IsReplayPresentation &&
        string.IsNullOrWhiteSpace(program.RehearsalLabel);
}
