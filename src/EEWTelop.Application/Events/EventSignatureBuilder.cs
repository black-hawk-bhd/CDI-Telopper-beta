using System.Security.Cryptography;
using System.Text.Json;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public sealed class EventSignatureBuilder : IEventSignatureBuilder
{
    public string Build(DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", disasterEvent.Provider);
            writer.WriteNumber("code", disasterEvent.ProviderCode);
            writer.WriteString("issuedAt", disasterEvent.IssuedAt.ToUniversalTime());
            writer.WriteBoolean("cancelled", disasterEvent.IsCancelled);

            switch (disasterEvent)
            {
                case QuakeEvent quake:
                    WriteQuake(writer, quake);
                    break;
                case TsunamiEvent tsunami:
                    WriteTsunami(writer, tsunami);
                    break;
                case EewEvent eew:
                    WriteEew(writer, eew);
                    break;
                case WeatherWarningEvent weather:
                    WriteWeatherWarning(writer, weather);
                    break;
                case VolcanoEvent volcano:
                    WriteVolcano(writer, volcano);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported disaster event type: {disasterEvent.GetType().FullName}");
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteQuake(Utf8JsonWriter writer, QuakeEvent quake)
    {
        WriteIssue(writer, quake.Issue);
        writer.WriteString("issueType", quake.IssueType.ToString());
        WriteEarthquake(writer, quake.Earthquake);
        writer.WriteStartArray("points");
        foreach (QuakePoint point in quake.Points
                     .OrderBy(static point => point.Prefecture, StringComparer.Ordinal)
                     .ThenBy(static point => point.Address, StringComparer.Ordinal)
                     .ThenBy(static point => point.Scale))
        {
            writer.WriteStartObject();
            writer.WriteString("prefecture", point.Prefecture);
            writer.WriteString("address", point.Address);
            writer.WriteBoolean("isArea", point.IsArea);
            writer.WriteNumber("scale", (int)point.Scale);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("headline", quake.Headline);
        if (quake.LongPeriodIntensity is { } longPeriod)
        {
            writer.WriteNumber("longPeriodMaximumClass", longPeriod.MaximumClass);
            writer.WriteStartArray("longPeriodAreas");
            foreach (LongPeriodIntensityArea area in longPeriod.Areas
                         .OrderBy(static area => area.Prefecture, StringComparer.Ordinal)
                         .ThenBy(static area => area.Area, StringComparer.Ordinal)
                         .ThenBy(static area => area.Class))
            {
                writer.WriteStartObject();
                writer.WriteString("prefecture", area.Prefecture);
                writer.WriteString("area", area.Area);
                writer.WriteNumber("class", area.Class);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteString("comment", quake.FreeFormComment);
    }

    private static void WriteTsunami(Utf8JsonWriter writer, TsunamiEvent tsunami)
    {
        WriteIssue(writer, tsunami.Issue);
        WriteNullableDateTime(writer, "observationAsOf", tsunami.ObservationAsOf);
        writer.WriteStartArray("areas");
        foreach (TsunamiArea area in tsunami.Areas
                     .OrderBy(static area => area.Role)
                     .ThenBy(static area => area.ParentAreaName, StringComparer.Ordinal)
                     .ThenBy(static area => area.Name, StringComparer.Ordinal)
                     .ThenBy(static area => area.Grade))
        {
            writer.WriteStartObject();
            writer.WriteString("name", area.Name);
            writer.WriteString("role", area.Role.ToString());
            writer.WriteString("parentArea", area.ParentAreaName);
            writer.WriteString("grade", area.Grade.ToString());
            writer.WriteBoolean("immediate", area.Immediate);
            WriteNullableDateTime(writer, "highTideAt", area.HighTideAt);
            writer.WriteString("condition", area.FirstHeight?.Condition ?? string.Empty);
            WriteNullableDateTime(writer, "arrivalTime", area.FirstHeight?.ArrivalTime);
            writer.WriteString("heightDescription", area.MaximumHeight?.Description ?? string.Empty);
            WriteNullableNumber(writer, "heightMeters", area.MaximumHeight?.ValueMeters);
            WriteNullableDateTime(writer, "observedAt", area.MaximumHeight?.ObservedAt);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteEew(Utf8JsonWriter writer, EewEvent eew)
    {
        WriteIssue(writer, eew.Issue);
        writer.WriteBoolean("test", eew.IsTest);
        if (eew.Earthquake is not null)
        {
            WriteEarthquake(writer, eew.Earthquake);
        }

        writer.WriteStartArray("areas");
        foreach (EewArea area in eew.Areas
                     .OrderBy(static area => area.Prefecture, StringComparer.Ordinal)
                     .ThenBy(static area => area.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("prefecture", area.Prefecture);
            writer.WriteString("name", area.Name);
            writer.WriteNumber("scaleFrom", (int)area.ScaleFrom);
            writer.WriteNumber("scaleTo", area.ScaleTo);
            writer.WriteNumber("kindCode", (int)area.WarningKind);
            WriteNullableDateTime(writer, "arrivalTime", area.ArrivalTime);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteWeatherWarning(
        Utf8JsonWriter writer,
        WeatherWarningEvent weather)
    {
        WriteIssue(writer, weather.Issue);
        writer.WriteString("informationType", weather.InformationType.ToString());
        writer.WriteString("headline", weather.Headline);
        WriteNullableDateTime(writer, "validUntil", weather.ValidUntil);
        writer.WriteStartArray("items");
        foreach (WeatherWarningItem item in weather.Items
                     .OrderBy(static item => item.AreaCode, StringComparer.Ordinal)
                     .ThenBy(static item => item.AreaName, StringComparer.Ordinal)
                     .ThenBy(static item => item.KindCode, StringComparer.Ordinal)
                     .ThenBy(static item => item.KindName, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("area", item.AreaName);
            writer.WriteString("areaCode", item.AreaCode);
            writer.WriteString("kind", item.KindName);
            writer.WriteString("kindCode", item.KindCode);
            writer.WriteNumber("level", (int)item.Level);
            writer.WriteString("status", item.Status);
            writer.WriteBoolean("active", item.IsActive);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteVolcano(Utf8JsonWriter writer, VolcanoEvent volcano)
    {
        WriteIssue(writer, volcano.Issue);
        writer.WriteString("informationType", volcano.InformationType.ToString());
        writer.WriteString("volcanoName", volcano.VolcanoName);
        writer.WriteString("volcanoCode", volcano.VolcanoCode);
        writer.WriteNumber("alertLevel", (int)volcano.AlertLevel);
        writer.WriteString("alertLevelText", volcano.AlertLevelText);
        writer.WriteString("alertLevelCode", volcano.AlertLevelCode);
        writer.WriteString("alertCondition", volcano.AlertCondition);
        writer.WriteString("previousAlertLevelText", volcano.PreviousAlertLevelText);
        writer.WriteString("previousAlertLevelCode", volcano.PreviousAlertLevelCode);
        writer.WriteBoolean("warning", volcano.IsWarning);
        writer.WriteString("headline", volcano.Headline);
        writer.WriteString("activity", volcano.Activity);
        writer.WriteString("prevention", volcano.Prevention);
        WriteNullableDateTime(writer, "eventTime", volcano.EventTime);
        writer.WriteBoolean("eventTimeIsApproximate", volcano.EventTimeIsApproximate);
        writer.WriteString("eventTimePrecision", volcano.EventTimePrecision);
        writer.WriteBoolean("telegramCancellation", volcano.IsTelegramCancellation);
        writer.WriteString("notice", volcano.Notice);
        writer.WriteString("otherInfo", volcano.OtherInfo);
        writer.WriteString("appendix", volcano.Appendix);
        writer.WriteString("contentText", volcano.ContentText);
        writer.WriteString("bodyText", volcano.BodyText);
        writer.WriteStartArray("targetAreas");
        foreach (VolcanoTargetArea area in volcano.TargetAreas
                     .OrderBy(static area => area.Code, StringComparer.Ordinal)
                     .ThenBy(static area => area.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", area.Name);
            writer.WriteString("code", area.Code);
            writer.WriteString("kind", area.KindName);
            writer.WriteString("kindCode", area.KindCode);
            writer.WriteString("status", area.Status);
            writer.WriteString("previousKind", area.PreviousKindName);
            writer.WriteString("previousKindCode", area.PreviousKindCode);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteIssue(Utf8JsonWriter writer, IssueInfo issue)
    {
        writer.WriteStartObject("issue");
        writer.WriteString("source", issue.Source);
        writer.WriteString("time", issue.IssuedAt.ToUniversalTime());
        writer.WriteString("type", issue.RawType);
        writer.WriteString("correct", issue.Correction.ToString());
        writer.WriteString("serial", issue.Serial ?? string.Empty);
        writer.WriteString("informationType", issue.InformationType);
        writer.WriteEndObject();
    }

    private static void WriteEarthquake(Utf8JsonWriter writer, EarthquakeInfo earthquake)
    {
        writer.WriteStartObject("earthquake");
        writer.WriteString("originTime", earthquake.OriginTime.ToUniversalTime());
        WriteNullableDateTime(writer, "arrivalTime", earthquake.ArrivalTime);
        writer.WriteNumber("maxScale", (int)earthquake.MaximumScale);
        writer.WriteString("domesticTsunami", earthquake.DomesticTsunami.ToString());
        writer.WriteString("foreignTsunami", earthquake.ForeignTsunami.ToString());

        if (earthquake.Hypocenter is not null)
        {
            HypocenterInfo hypocenter = earthquake.Hypocenter;
            writer.WriteStartObject("hypocenter");
            writer.WriteString("name", hypocenter.Name);
            writer.WriteString("reducedName", hypocenter.ReducedName);
            WriteNullableNumber(writer, "latitude", hypocenter.Latitude);
            WriteNullableNumber(writer, "longitude", hypocenter.Longitude);
            if (hypocenter.DepthKilometers is int depth)
            {
                writer.WriteNumber("depth", depth);
            }
            else
            {
                writer.WriteNull("depth");
            }

            WriteNullableNumber(writer, "magnitude", hypocenter.Magnitude);
            writer.WriteString("condition", hypocenter.Condition);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteNullableDateTime(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is DateTimeOffset dateTime)
        {
            writer.WriteString(propertyName, dateTime.ToUniversalTime());
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string propertyName, double? value)
    {
        if (value is double number && double.IsFinite(number))
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }
}
