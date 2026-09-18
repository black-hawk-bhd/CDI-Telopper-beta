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
        void AddText(string badge, IEnumerable<string> lines, string? pageStyle = null)
        {
            foreach (string line in lines)
                foreach (string fragment in WeatherTextPagination.Split(line,
                    flood.Districts.SelectMany(d => new[] { d.City, d.Prefecture }).Append(flood.RiverName), 78))
                    pages.Add(CreateTextPage($"{flood.RiverName}｜{badge}", [fragment], pageStyle ?? style));
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
                // Keep each municipality attached to its district details, including on continuation pages.
                foreach (RiverFloodDistrict district in districts.Distinct())
                {
                    string station = district.Station == district.Prefecture + district.City ||
                        district.Station == district.Prefecture ? string.Empty : district.Station;
                    string heading = string.Join("　", new[] { station, district.Prefecture, district.City }
                        .Where(s => s.Length > 0));
                    if (string.IsNullOrWhiteSpace(district.SubCities) || district.SubCities == "-")
                    {
                        pages.Add(CreateTextPage($"{flood.RiverName}｜浸水想定地区", [heading], style));
                        continue;
                    }

                    // Whitespace separates district names in the XML. Do not join them into an
                    // oversized parenthesis that can be cut across unrelated municipalities.
                    foreach (string detail in WeatherTextPagination.Split(district.SubCities.Trim(),
                        Regex.Split(district.SubCities.Trim(), @"\s+"), 78))
                        pages.Add(CreateTextPage($"{flood.RiverName}｜浸水想定地区｜{heading}",
                            [detail], style));
                }
            }
            if (flood.AlertLevel >= 4)
            {
                // Preserve the actual telegram narrative rather than a fixed evacuation message.
                string stationBadge = flood.Badge;
                string stationStyle = style;
                foreach (string mainText in flood.MainTexts)
                {
                    // Carry the station's explicitly stated level across its continuation pages.
                    // Do not interpret a previous level in a transition sentence as the current level.
                    Match level = Regex.Match(mainText, @"^\s*【\s*警戒レベル\s*([2-5２-５])\s*(相当)?\s*】");
                    if (level.Success)
                    {
                        char digit = level.Groups[1].Value[0];
                        int value = digit >= '２' ? digit - '０' : digit - '0';
                        stationBadge = "警戒レベル" + level.Groups[1].Value + level.Groups[2].Value;
                        stationStyle = value switch
                        {
                            2 => DisplayStyleTokens.WeatherAdvisory,
                            4 => DisplayStyleTokens.WeatherDangerWarning,
                            5 => DisplayStyleTokens.WeatherSpecialWarning,
                            _ => DisplayStyleTokens.WeatherWarning,
                        };
                    }
                    AddText(stationBadge, [mainText], stationStyle);
                }
            }
        }
        return PageComposerSupport.CreateProgram(weather, settings,
            flood.AlertLevel == 2 || weather.IsCancelled ? OverlayPriority.WeatherAdvisory : OverlayPriority.WeatherWarning,
            EndPolicy.AutoHide, pages.ToArray());
    }
}
