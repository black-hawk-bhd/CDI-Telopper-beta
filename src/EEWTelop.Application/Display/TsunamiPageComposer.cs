using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Display;

internal static class TsunamiPageComposer
{
    private const int AreasPerPage = 3;

    private static readonly TsunamiGrade[] GradeOrder =
    [
        TsunamiGrade.MajorWarning,
        TsunamiGrade.Warning,
        TsunamiGrade.Watch,
        TsunamiGrade.Forecast,
        TsunamiGrade.Unknown,
    ];

    public static DisplayProgram Compose(TsunamiEvent tsunami, DisplaySettings settings)
    {
        if (PageComposerSupport.IsTelegramCancellation(tsunami.Issue))
        {
            var telegramCancelBlock = new DisplayBlock(
                "取消",
                PageComposerSupport.GetCancellationText(
                    PageComposerSupport.GetTsunamiCancellationSubject(tsunami)),
                string.Empty,
                DisplayStyleTokens.TsunamiCancel);
            return PageComposerSupport.CreateProgram(
                tsunami,
                settings,
                OverlayPriority.TsunamiCancel,
                EndPolicy.AutoHide,
                [new PageDraft([telegramCancelBlock], TimeSpan.FromSeconds(20))]);
        }

        if (tsunami.IsCancelled)
        {
            var cancelBlock = new DisplayBlock(
                string.Empty,
                "津波注意報・津波警報・大津波警報はすべて解除されました",
                string.Empty,
                DisplayStyleTokens.TsunamiCancel);
            return PageComposerSupport.CreateProgram(
                tsunami,
                settings,
                OverlayPriority.TsunamiCancel,
                EndPolicy.AutoHide,
                [new PageDraft([cancelBlock], TimeSpan.FromSeconds(20))]);
        }

        TsunamiArea[] forecastAreas = GradeOrder
            .SelectMany(grade => tsunami.Areas.Where(area =>
                area.Role == TsunamiInformationRole.ForecastArea &&
                area.Grade == grade &&
                (settings.ShowTsunamiForecast || area.Grade != TsunamiGrade.Forecast)))
            .ToArray();
        var pages = new List<PageDraft>();

        TsunamiArea[] coastalObservations = tsunami.Areas
            .Where(static area => area.Role == TsunamiInformationRole.CoastalObservation)
            .ToArray();
        TsunamiArea[] offshoreObservations = tsunami.Areas
            .Where(static area => area.Role == TsunamiInformationRole.OffshoreObservation)
            .ToArray();
        bool hasObservations = coastalObservations.Length > 0 || offshoreObservations.Length > 0;
        bool isObservationTelegram = tsunami.Issue.RawType is "VTSE51" or "VTSE52";
        if (tsunami.WarningStateChanged)
        {
            pages.Add(BuildWarningChangedPage());
        }

        if (tsunami.Issue.RawType == "VTSE51" &&
            hasObservations &&
            tsunami.ObservationAsOf is { } observationAsOf)
        {
            pages.Add(BuildObservationSummaryPage(observationAsOf, forecastAreas));
        }

        if (isObservationTelegram)
        {
            AddObservationPages(pages, coastalObservations, offshoreObservations);
            AddArrivalForecastPages(pages, tsunami);
            pages.AddRange(BuildForecastAreaPages(forecastAreas));
        }
        else
        {
            // A new VTSE41 warning update must remain action-first even if the
            // accumulator still contains older observation sections.
            if (tsunami.Issue.RawType == "VTSE41" &&
                BuildWarningIntroductionPage(forecastAreas) is { } introductionPage)
            {
                pages.Add(introductionPage);
            }

            pages.AddRange(BuildForecastAreaPages(forecastAreas));
            AddArrivalForecastPages(pages, tsunami);
            AddObservationPages(pages, coastalObservations, offshoreObservations);
        }

        bool persistent = tsunami.SourceMode == SourceMode.Production && pages.Count > 0;
        return PageComposerSupport.CreateProgram(
            tsunami,
            settings,
            GetPriority(forecastAreas),
            persistent ? EndPolicy.LoopUntilReplaced : EndPolicy.AutoHide,
            pages);
    }

    private static PageDraft BuildWarningChangedPage() => new(
        [new DisplayBlock(
            "津波情報",
            "津波情報が変更されました",
            string.Empty,
            DisplayStyleTokens.Tsunami)]);

    private static PageDraft? BuildWarningIntroductionPage(
        IReadOnlyCollection<TsunamiArea> forecastAreas)
    {
        TsunamiGrade highest = GradeOrder.FirstOrDefault(grade =>
            grade is TsunamiGrade.MajorWarning or TsunamiGrade.Warning or TsunamiGrade.Watch &&
            forecastAreas.Any(area => area.Grade == grade));
        string primary = highest switch
        {
            TsunamiGrade.MajorWarning =>
                "大津波警報・津波警報・津波注意報は次の通りです",
            TsunamiGrade.Warning => "津波警報・津波注意報は次の通りです",
            TsunamiGrade.Watch => "津波注意報は次の通りです",
            _ => string.Empty,
        };
        return string.IsNullOrWhiteSpace(primary)
            ? null
            : new PageDraft(
                [new DisplayBlock("津波情報", primary, string.Empty, DisplayStyleTokens.Tsunami)]);
    }

    private static void AddObservationPages(
        List<PageDraft> pages,
        IEnumerable<TsunamiArea> coastalObservations,
        IEnumerable<TsunamiArea> offshoreObservations)
    {
        pages.AddRange(BuildDetailPages(
            coastalObservations,
            TsunamiInformationRole.CoastalObservation,
            "津波観測情報"));
        pages.AddRange(BuildDetailPages(
            offshoreObservations,
            TsunamiInformationRole.OffshoreObservation,
            "津波観測情報"));
    }

    private static void AddArrivalForecastPages(List<PageDraft> pages, TsunamiEvent tsunami) =>
        pages.AddRange(BuildDetailPages(
            GetPendingStationForecasts(tsunami),
            TsunamiInformationRole.StationForecast,
            "津波到達予想"));

    private static PageDraft BuildObservationSummaryPage(
        DateTimeOffset observationAsOf,
        IReadOnlyCollection<TsunamiArea> forecastAreas)
    {
        string referenceTime = observationAsOf
            .ToOffset(TimeSpan.FromHours(9))
            .ToString("HH時mm分", CultureInfo.InvariantCulture);
        var blocks = new List<DisplayBlock>(2)
        {
            new(
                "津波観測情報",
                $"{referenceTime}現在の津波観測値をお知らせします",
                string.Empty,
                DisplayStyleTokens.Tsunami),
        };

        if (BuildActiveWarningBlock(forecastAreas) is { } warningBlock)
        {
            blocks.Add(warningBlock);
        }

        return new PageDraft(blocks);
    }

    private static DisplayBlock? BuildActiveWarningBlock(IEnumerable<TsunamiArea> forecastAreas)
    {
        TsunamiGrade highest = GradeOrder.FirstOrDefault(grade =>
            grade != TsunamiGrade.Forecast && forecastAreas.Any(area => area.Grade == grade));
        return highest switch
        {
            TsunamiGrade.MajorWarning => CreateWarningBlock("大津波警報"),
            TsunamiGrade.Warning => CreateWarningBlock("津波警報"),
            TsunamiGrade.Watch => CreateWarningBlock("津波注意報"),
            _ => null,
        };
    }

    private static DisplayBlock CreateWarningBlock(string badge) =>
        new(badge, "現在発表中", string.Empty, DisplayStyleTokens.Tsunami);

    private static IEnumerable<TsunamiArea> GetPendingStationForecasts(TsunamiEvent tsunami)
    {
        return tsunami.Areas.Where(area =>
            area.Role == TsunamiInformationRole.StationForecast &&
            !HasArrivalBeenConfirmed(area));
    }

    private static bool HasArrivalBeenConfirmed(TsunamiArea area)
    {
        string condition = area.FirstHeight?.Condition ?? string.Empty;
        return condition.Contains("到達を確認", StringComparison.Ordinal) ||
            condition.Contains("既に津波到達", StringComparison.Ordinal) ||
            condition.Contains("既に到達", StringComparison.Ordinal);
    }

    private static List<PageDraft> BuildForecastAreaPages(TsunamiArea[] areas)
    {
        var pages = new List<PageDraft>((areas.Length + AreasPerPage - 1) / AreasPerPage);
        foreach (TsunamiGrade grade in GradeOrder)
        {
            TsunamiArea[] gradeAreas = areas.Where(area => area.Grade == grade).ToArray();
            for (int offset = 0; offset < gradeAreas.Length; offset += AreasPerPage)
            {
                TsunamiArea[] items = gradeAreas.Skip(offset).Take(AreasPerPage).ToArray();
                var blocks = new DisplayBlock[items.Length];
                for (int index = 0; index < items.Length; index++)
                {
                    TsunamiArea area = items[index];
                    blocks[index] = new DisplayBlock(
                        index == 0 ? GetGradeLabel(area.Grade) : string.Empty,
                        string.IsNullOrWhiteSpace(area.Name) ? "予報区不明" : area.Name,
                        BuildForecastNote(area),
                        DisplayStyleTokens.Tsunami);
                }

                pages.Add(new PageDraft(blocks));
            }
        }

        return pages;
    }

    private static IEnumerable<PageDraft> BuildDetailPages(
        IEnumerable<TsunamiArea> source,
        TsunamiInformationRole role,
        string badge)
    {
        TsunamiArea[] areas = source
            .Where(area => area.Role == role)
            .ToArray();
        for (int offset = 0; offset < areas.Length; offset += AreasPerPage)
        {
            TsunamiArea[] items = areas.Skip(offset).Take(AreasPerPage).ToArray();
            var blocks = new DisplayBlock[items.Length];
            for (int index = 0; index < items.Length; index++)
            {
                TsunamiArea area = items[index];
                blocks[index] = new DisplayBlock(
                    index == 0 ? badge : string.Empty,
                    area.Name,
                    BuildDetailNote(area),
                    DisplayStyleTokens.Tsunami);
            }

            yield return new PageDraft(blocks);
        }
    }

    private static OverlayPriority GetPriority(IEnumerable<TsunamiArea> areas)
    {
        TsunamiGrade[] grades = areas.Select(static area => area.Grade).Distinct().ToArray();
        if (grades.Contains(TsunamiGrade.MajorWarning) || grades.Contains(TsunamiGrade.Warning))
        {
            return OverlayPriority.TsunamiWarning;
        }

        return grades.Contains(TsunamiGrade.Watch)
            ? OverlayPriority.TsunamiWatch
            : OverlayPriority.UnknownTsunami;
    }

    private static string GetGradeLabel(TsunamiGrade grade) => grade switch
    {
        TsunamiGrade.MajorWarning => "大津波警報",
        TsunamiGrade.Warning => "津波警報",
        TsunamiGrade.Watch => "津波注意報",
        TsunamiGrade.Forecast => "津波予報（若干の海面変動）",
        _ => "津波情報",
    };

    private static string BuildForecastNote(TsunamiArea area)
    {
        var parts = new List<string>(2);
        if (area.FirstHeight is not null)
        {
            if (!string.IsNullOrWhiteSpace(area.FirstHeight.Condition))
            {
                parts.Add(area.FirstHeight.Condition);
            }
            else if (area.FirstHeight.ArrivalTime is DateTimeOffset arrivalTime)
            {
                parts.Add(PageComposerSupport.FormatJapanTime(arrivalTime));
            }
        }

        if (area.MaximumHeight is not null)
        {
            if (!string.IsNullOrWhiteSpace(area.MaximumHeight.Description))
            {
                parts.Add(area.MaximumHeight.Description);
            }
            else if (area.MaximumHeight.ValueMeters is double height)
            {
                parts.Add(height.ToString("0.#", CultureInfo.InvariantCulture) + "m");
            }

            if (!string.IsNullOrWhiteSpace(area.MaximumHeight.Condition) &&
                !parts.Contains(area.MaximumHeight.Condition, StringComparer.Ordinal))
            {
                parts.Add(area.MaximumHeight.Condition);
            }
        }

        return parts.Count == 0 ? string.Empty : $"〔{string.Join(' ', parts)}〕";
    }

    private static string BuildDetailNote(TsunamiArea area)
    {
        var parts = new List<string>(4);
        if (area.Role == TsunamiInformationRole.StationForecast)
        {
            if (area.FirstHeight?.ArrivalTime is DateTimeOffset arrivalTime)
            {
                parts.Add("到達 " + PageComposerSupport.FormatJapanTime(arrivalTime));
            }

            if (!string.IsNullOrWhiteSpace(area.FirstHeight?.Condition))
            {
                parts.Add(area.FirstHeight.Condition);
            }

            if (area.HighTideAt is DateTimeOffset highTideAt)
            {
                parts.Add("満潮 " + PageComposerSupport.FormatJapanTime(highTideAt));
            }
        }
        else
        {
            if (area.MaximumHeight?.ObservedAt is DateTimeOffset observedAt)
            {
                parts.Add(PageComposerSupport.FormatJapanTime(observedAt));
            }

            if (!string.IsNullOrWhiteSpace(area.FirstHeight?.Condition))
            {
                parts.Add(area.FirstHeight.Condition);
            }
        }

        if (area.MaximumHeight is not null)
        {
            if (!string.IsNullOrWhiteSpace(area.MaximumHeight.Description))
            {
                parts.Add(area.MaximumHeight.Description);
            }
            else if (area.MaximumHeight.ValueMeters is double height)
            {
                parts.Add(height.ToString("0.#", CultureInfo.InvariantCulture) + "m");
            }

            if (!string.IsNullOrWhiteSpace(area.MaximumHeight.Condition) &&
                !parts.Contains(area.MaximumHeight.Condition, StringComparer.Ordinal))
            {
                parts.Add(area.MaximumHeight.Condition);
            }
        }

        return parts.Count == 0 ? string.Empty : $"〔{string.Join(' ', parts)}〕";
    }
}
