using System.Xml.Linq;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Dmdata.Normalization;

public sealed partial class JmaXmlEventNormalizer
{
    private static bool IsRiverFloodTelegram(string type) => type.Length == 6 &&
        type.StartsWith("VXKO", StringComparison.Ordinal) &&
        int.TryParse(type.AsSpan(4), out int number) && number is >= 50 and <= 89;

    private NormalizeResult NormalizeRiverFlood(RawProviderMessage raw, XDocument document,
        string telegramType)
    {
        XElement head = RequiredDescendant(document, "Head");
        XElement? forecast = Descendants(head, "Information").FirstOrDefault(e =>
            AttributeText(e, "type") == "指定河川洪水予報（予報区域）");
        XElement? kind = Descendant(forecast, "Kind");
        string code = Text(Descendant(kind, "Code"));
        string originalName = Text(Descendant(kind, "Name"));
        int level = code switch
        {
            "10" or "20" or "21" or "22" => 2,
            "30" or "31" => 3,
            "40" or "41" => 4,
            "51" or "53" => 5,
            _ => 0,
        };
        string badge = level switch
        {
            2 => "レベル２氾濫注意報",
            3 => "レベル３氾濫警報",
            4 => "レベル４氾濫危険警報",
            5 => "レベル５氾濫特別警報",
            _ => string.IsNullOrWhiteSpace(originalName) ? "指定河川洪水予報" : originalName,
        };
        if (code == "53") badge += "（氾濫水の予報）";
        if (code == "22") badge += "（警報解除）";
        string river = Text(Descendant(Descendant(forecast, "Area"), "Name"));
        if (string.IsNullOrWhiteSpace(river)) river = Text(Descendant(head, "Title"));
        string infoType = Text(Descendant(head, "InfoType"));
        // Code 22 retains the advisory: a warning downgrade is not an all-clear.
        bool cancelled = code == "10" || infoType.Contains("取消", StringComparison.Ordinal);
        XElement warning = Descendants(document, "Warning").FirstOrDefault(e =>
            AttributeText(e, "type") == "指定河川洪水予報") ?? new XElement("Warning");
        string[] mainTexts = Descendants(warning, "Property")
            .Where(e => Text(e.Element(e.Name.Namespace + "Type")) == "主文")
            .Select(e => Text(Descendant(e, "Text"))).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).ToArray();
        RiverFloodDistrict[] districts = Descendants(warning, "Item")
            .Where(e => Descendants(e, "Property").Any(p =>
                Text(Descendant(p, "Type")).StartsWith("浸水想定地区", StringComparison.Ordinal)))
            .SelectMany(e => Descendants(e, "Area"))
            .Select(e => new RiverFloodDistrict(Text(Descendant(e, "Name")),
                Text(Descendant(e, "Prefecture")), Text(Descendant(e, "PrefectureCode")),
                Text(Descendant(e, "City")), Text(Descendant(e, "CityCode")),
                Text(Descendant(e, "SubCityList"))))
            .Where(d => d.City.Length > 0 || d.Prefecture.Length > 0).Distinct().ToArray();
        WeatherWarningLevel severity = level switch
        {
            2 => WeatherWarningLevel.Advisory,
            5 => WeatherWarningLevel.SpecialWarning,
            _ => WeatherWarningLevel.Warning,
        };
        var areas = Descendants(head, "Information").Where(e =>
                AttributeText(e, "type") == "指定河川洪水予報（府県予報区等）")
            .SelectMany(e => Descendants(e, "Area"))
            .Select(e => (Name: Text(Descendant(e, "Name")), Code: Text(Descendant(e, "Code"))))
            .Concat(districts.Select(d => (Name: d.Prefecture, Code: d.PrefectureCode)))
            .Where(a => a.Name.Length > 0).DistinctBy(a => a.Name).ToArray();
        if (areas.Length == 0) areas = [(river, string.Empty)];
        WeatherWarningItem[] items = areas.Select(a => new WeatherWarningItem(a.Name, a.Code,
            badge, code, severity, cancelled ? "解除" : infoType, !cancelled)).ToArray();
        DateTimeOffset issuedAt = ReadDateTime(head, "ReportDateTime") ?? raw.ReceivedAt;
        var issue = new IssueInfo(Text(Descendant(document, "PublishingOffice")), issuedAt,
            telegramType, ReadCorrection(document), NullIfEmpty(Text(Descendant(head, "Serial"))), infoType);
        string headline = ReadHeadlineText(head);
        if (string.IsNullOrWhiteSpace(headline)) headline = river;
        string eventId = Text(Descendant(head, "EventID"));
        var result = new WeatherWarningEvent(EventId.Create("river-flood-" +
                (eventId.Length > 0 ? eventId : river)), raw.Provider, issuedAt, raw.ReceivedAt,
            string.Empty, IsTestTelegram(document) && raw.SourceMode == SourceMode.Production
                ? SourceMode.ManualTest : raw.SourceMode,
            issue, headline, items, cancelled, WeatherInformationType.RiverFlood,
            ReadDateTime(head, "ValidDateTime"))
        {
            RiverFlood = new RiverFloodInfo(river, badge, level, districts, mainTexts),
        };
        return NormalizeResult.Success(result with { Signature = _signatureBuilder.Build(result) });
    }
}
