using System.Text.RegularExpressions;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static partial class WeatherWarningPageComposer
{
    private static DisplayProgram ComposeRiverFlood(WeatherWarningEvent weather, DisplaySettings settings)
    {
        RiverFloodInfo flood = weather.RiverFlood!;
        string style = weather.IsCancelled ? DisplayStyleTokens.WeatherCancel : flood.AlertLevel switch
        {
            5 => DisplayStyleTokens.WeatherSpecialWarning,
            4 => DisplayStyleTokens.WeatherDangerWarning,
            2 => DisplayStyleTokens.WeatherAdvisory,
            _ => DisplayStyleTokens.WeatherWarning,
        };
        var pages = new List<PageDraft>();
        void AddText(string badge, IEnumerable<string> lines)
        {
            foreach (IReadOnlyList<string> page in NarrativeTextPaginator.Paginate(lines, 2))
                pages.Add(CreateTextPage(badge, page.ToArray(), style));
        }
        if (weather.IsCancelled)
        {
            AddText("解除", [$"{flood.RiverName}の氾濫注意報は解除されました。"]);
        }
        else
        {
            string headline = Regex.Replace(weather.Headline, "^\\s*【[^】]*】\\s*", string.Empty);
            if (!headline.Contains(flood.RiverName, StringComparison.Ordinal))
                headline = flood.RiverName + " " + headline;
            AddText(flood.Badge, [headline]);
            RiverFloodDistrict[] districts = flood.Districts.Where(d => weather.Items.Any(i =>
                i.AreaName == d.Prefecture || (d.PrefectureCode.Length > 0 &&
                i.AreaCode.StartsWith(d.PrefectureCode, StringComparison.Ordinal)))).ToArray();
            if (districts.Length > 0)
            {
                AddText(string.Empty, ["氾濫による浸水が想定される地区は次の通りです"]);
                var lines = districts.GroupBy(d => (Station: d.Station == d.Prefecture + d.City ||
                        d.Station == d.Prefecture ? string.Empty : d.Station, d.Prefecture))
                    .Select(g => string.Join("　", new[] { g.Key.Station, g.Key.Prefecture }
                        .Where(s => s.Length > 0)
                        .Concat(g.Select(d => d.City + (d.SubCities.Length > 0 && d.SubCities != "-" ? $"（{d.SubCities}）" : ""))
                            .Distinct(StringComparer.Ordinal))));
                AddText(string.Empty, lines);
            }
            if (flood.AlertLevel >= 4)
            {
                // Preserve the actual telegram narrative rather than a fixed evacuation message.
                AddText(flood.Badge, flood.MainTexts);
            }
        }
        return PageComposerSupport.CreateProgram(weather, settings,
            flood.AlertLevel == 2 || weather.IsCancelled ? OverlayPriority.WeatherAdvisory : OverlayPriority.WeatherWarning,
            EndPolicy.AutoHide, pages.ToArray());
    }
}
