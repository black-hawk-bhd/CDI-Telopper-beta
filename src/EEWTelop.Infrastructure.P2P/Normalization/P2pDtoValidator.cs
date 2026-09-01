using EEWTelop.Application.Events;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Dtos;

namespace EEWTelop.Infrastructure.P2P.Normalization;

internal static class P2pDtoValidator
{
    private static readonly HashSet<string> QuakeIssueTypes =
    [
        "ScalePrompt",
        "Destination",
        "ScaleAndDestination",
        "DetailScale",
        "Foreign",
        "Other",
    ];

    private static readonly HashSet<string> CorrectionTypes =
    [
        "None",
        "Unknown",
        "ScaleOnly",
        "DestinationOnly",
        "ScaleAndDestination",
    ];

    private static readonly HashSet<string> DomesticTsunamiValues =
    ["None", "Unknown", "Checking", "NonEffective", "Watch", "Warning"];

    private static readonly HashSet<string> ForeignTsunamiValues =
    [
        "None",
        "Unknown",
        "Checking",
        "NonEffectiveNearby",
        "WarningNearby",
        "WarningPacific",
        "WarningPacificWide",
        "WarningIndian",
        "WarningIndianWide",
        "Potential",
    ];

    private static readonly HashSet<string> TsunamiGrades =
    ["MajorWarning", "Warning", "Watch", "Forecast", "Unknown"];

    private static readonly HashSet<string> EewKindCodes = ["10", "11", "19"];

    public static IReadOnlyList<ValidationIssue> Validate(P2pQuakeDto dto)
    {
        var issues = ValidateBase(dto, 551);
        ValidateIssue(dto.Issue, issues, requireSource: false);

        if (dto.Issue is not null)
        {
            WarnUnknown(issues, "issue.type", dto.Issue.Type ?? string.Empty, QuakeIssueTypes);
            if (dto.Issue.Correct is not null)
            {
                WarnUnknown(issues, "issue.correct", dto.Issue.Correct, CorrectionTypes);
            }
        }

        if (dto.Earthquake is null)
        {
            AddRequired(issues, "earthquake");
        }
        else
        {
            ValidateDate(dto.Earthquake.Time, "earthquake.time", issues);
            WarnUnknownOptional(
                issues,
                "earthquake.domesticTsunami",
                dto.Earthquake.DomesticTsunami,
                DomesticTsunamiValues);
            WarnUnknownOptional(
                issues,
                "earthquake.foreignTsunami",
                dto.Earthquake.ForeignTsunami,
                ForeignTsunamiValues);
        }

        if (dto.Comments is null)
        {
            AddRequired(issues, "comments");
        }

        if (dto.Points is not null)
        {
            for (int index = 0; index < dto.Points.Count; index++)
            {
                P2pQuakePointDto point = dto.Points[index];
                RequireText(point.Prefecture, $"points[{index}].pref", issues);
                RequireText(point.Address, $"points[{index}].addr", issues);
                if (point.IsArea is null)
                {
                    AddRequired(issues, $"points[{index}].isArea");
                }

                if (point.Scale is null)
                {
                    AddRequired(issues, $"points[{index}].scale");
                }
                else if (ScaleFormatter.Normalize(point.Scale) == JmaScale.Unknown)
                {
                    AddWarning(issues, $"points[{index}].scale", "Unknown scale value.");
                }
            }
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> Validate(P2pTsunamiDto dto)
    {
        var issues = ValidateBase(dto, 552);
        ValidateIssue(dto.Issue, issues, requireSource: true);
        if (dto.Cancelled is null)
        {
            AddRequired(issues, "cancelled");
        }

        if (dto.Areas is not null)
        {
            for (int index = 0; index < dto.Areas.Count; index++)
            {
                P2pTsunamiAreaDto area = dto.Areas[index];
                RequireText(area.Name, $"areas[{index}].name", issues);
                WarnUnknownOptional(issues, $"areas[{index}].grade", area.Grade, TsunamiGrades);
            }
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> Validate(P2pEewDto dto)
    {
        var issues = ValidateBase(dto, 556);
        if (dto.Cancelled is null)
        {
            AddRequired(issues, "cancelled");
        }

        if (dto.Issue is null)
        {
            AddRequired(issues, "issue");
        }
        else
        {
            ValidateDate(dto.Issue.Time, "issue.time", issues);
            RequireText(dto.Issue.EventId, "issue.eventId", issues);
            RequireText(dto.Issue.Serial, "issue.serial", issues);
        }

        if (dto.Earthquake is not null)
        {
            ValidateDate(dto.Earthquake.OriginTime, "earthquake.originTime", issues);
            ValidateDate(dto.Earthquake.ArrivalTime, "earthquake.arrivalTime", issues);
            if (dto.Earthquake.Hypocenter is null)
            {
                AddRequired(issues, "earthquake.hypocenter");
            }
        }
        else if (dto.Cancelled is false)
        {
            AddRequired(issues, "earthquake");
        }

        if (dto.Areas is not null)
        {
            for (int index = 0; index < dto.Areas.Count; index++)
            {
                P2pEewAreaDto area = dto.Areas[index];
                RequireText(area.Prefecture, $"areas[{index}].pref", issues);
                RequireText(area.Name, $"areas[{index}].name", issues);
                if (area.ScaleFrom is null)
                {
                    AddRequired(issues, $"areas[{index}].scaleFrom");
                }

                if (area.ScaleTo is null)
                {
                    AddRequired(issues, $"areas[{index}].scaleTo");
                }

                if (string.IsNullOrWhiteSpace(area.KindCode))
                {
                    AddWarning(issues, $"areas[{index}].kindCode", "Warning kind is absent.");
                }
                else
                {
                    WarnUnknown(issues, $"areas[{index}].kindCode", area.KindCode, EewKindCodes);
                }

                if (!string.IsNullOrWhiteSpace(area.ArrivalTime))
                {
                    ValidateDate(area.ArrivalTime, $"areas[{index}].arrivalTime", issues);
                }
            }
        }

        return issues;
    }

    private static List<ValidationIssue> ValidateBase(P2pBasicDto dto, int expectedCode)
    {
        var issues = new List<ValidationIssue>();
        RequireText(dto.EffectiveId, "id", issues);
        ValidateDate(dto.Time, "time", issues);
        if (dto.Code != expectedCode)
        {
            issues.Add(new ValidationIssue(
                "code",
                $"Expected code {expectedCode}.",
                ValidationSeverity.Error));
        }

        return issues;
    }

    private static void ValidateIssue(
        P2pIssueDto? issue,
        ICollection<ValidationIssue> issues,
        bool requireSource)
    {
        if (issue is null)
        {
            AddRequired(issues, "issue");
            return;
        }

        if (requireSource)
        {
            RequireText(issue.Source, "issue.source", issues);
        }

        ValidateDate(issue.Time, "issue.time", issues);
        RequireText(issue.Type, "issue.type", issues);
    }

    private static void ValidateDate(
        string? value,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            AddRequired(issues, path);
        }
        else if (!P2pDateTimeParser.TryParse(value, out _))
        {
            issues.Add(new ValidationIssue(path, "Invalid date/time value.", ValidationSeverity.Error));
        }
    }

    private static void RequireText(
        string? value,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            AddRequired(issues, path);
        }
    }

    private static void WarnUnknownOptional(
        ICollection<ValidationIssue> issues,
        string path,
        string? value,
        IReadOnlySet<string> knownValues)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            WarnUnknown(issues, path, value, knownValues);
        }
    }

    private static void WarnUnknown(
        ICollection<ValidationIssue> issues,
        string path,
        string value,
        IReadOnlySet<string> knownValues)
    {
        if (!knownValues.Contains(value))
        {
            AddWarning(issues, path, $"Unknown value '{value}'.");
        }
    }

    private static void AddRequired(ICollection<ValidationIssue> issues, string path) =>
        issues.Add(new ValidationIssue(path, "Required value is missing.", ValidationSeverity.Error));

    private static void AddWarning(
        ICollection<ValidationIssue> issues,
        string path,
        string message) =>
        issues.Add(new ValidationIssue(path, message, ValidationSeverity.Warning));
}
