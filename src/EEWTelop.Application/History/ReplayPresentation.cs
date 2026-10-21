using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;

namespace EEWTelop.Application.History;

/// <summary>Presentation only: never reclassifies an event or erases its original label/badges.</summary>
public static class ReplayPresentation
{
    public static DisplayProgram Apply(
        DisplayProgram program,
        HistorySettings settings,
        DateTimeOffset issuedAt,
        bool isReplay,
        bool showPageIndicator)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(settings);
        program = program with
        {
            HideReplayModeBanner = settings.HideModeBanner,
            HideInformationBadges = settings.HideInformationBadges,
            IsReplayPresentation = isReplay,
        };
        if (!isReplay || !settings.AppendIssuedAtPage || program.Pages.Count == 0)
            return program;

        // Always use the telegram's announcement time in JST, not its receipt or replay time.
        string text = "これは" + issuedAt.ToOffset(TimeSpan.FromHours(9))
            .ToString("M月d日HH時mm分", CultureInfo.InvariantCulture) + "発表の情報です";
        DisplayPage notice = new(0,
            [new DisplayBlock(string.Empty, text, string.Empty, DisplayStyleTokens.Comment)], text, null);
        DisplayPage[] pages = program.Pages.Append(notice).ToArray();
        for (int index = 0; index < pages.Length; index++)
        {
            DisplayBlock[] blocks = pages[index].Blocks
                .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator).ToArray();
            if (showPageIndicator)
                blocks = [.. blocks, new DisplayBlock(string.Empty, $"{index + 1} / {pages.Length}",
                    string.Empty, DisplayStyleTokens.PageIndicator)];
            pages[index] = pages[index] with { Index = index + 1, Blocks = blocks };
        }
        return program with { Pages = pages };
    }
}
