using System.Globalization;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SharedExternalApiContractTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static JsonDocument LoadFixture() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "contracts", "external-api-v1-phase3.json")));

    [TestMethod]
    public void SharedSimulatorItemsRemainExcludedFromProductionApi()
    {
        using JsonDocument fixture = LoadFixture();
        var earthquakeState = new ExternalEarthquakeState();
        var tsunamiState = new ExternalApiState();
        foreach (var (kind, name) in new[] { ("earthquake", "earthquakeItem"),
                     ("eew", "eewItem"), ("tsunami", "tsunamiForecastItem"),
                     ("tsunami", "tsunamiObservationItem") })
        {
            DisasterEvent item = DisasterSimulatorDecoder.Decode(kind,
                fixture.RootElement.GetProperty(name), DateTimeOffset.UtcNow);
            Assert.AreEqual(SourceMode.ManualTest, item.SourceMode);
            var result = new EventIngestionResult(EventIngestionStatus.Accepted, item, null, null, []);
            earthquakeState.Observe(result);
            tsunamiState.Observe(result);
        }
        using JsonDocument quake = JsonDocument.Parse(JsonSerializer.Serialize(earthquakeState.ReadQuake()));
        using JsonDocument eew = JsonDocument.Parse(JsonSerializer.Serialize(earthquakeState.ReadEew(DateTimeOffset.UtcNow)));
        Assert.IsFalse(quake.RootElement.GetProperty("hasInformation").GetBoolean());
        Assert.IsFalse(eew.RootElement.GetProperty("hasInformation").GetBoolean());
        Assert.IsFalse(tsunamiState.Read(DateTimeOffset.UtcNow).HasForecast);
        Assert.IsNull(tsunamiState.Read(DateTimeOffset.UtcNow).Observation);
    }

    [TestMethod]
    public void DisplayCompositionDoesNotModifyApiPointNamesOrSignatures()
    {
        using JsonDocument fixture = LoadFixture();
        var original = (QuakeEvent)DisasterSimulatorDecoder.Decode("earthquake",
            fixture.RootElement.GetProperty("earthquakeItem"), DateTimeOffset.UtcNow);
        var point = original.Points[0] with
        { Address = "輪島市観測点＊", DisplayName = "輪島市観測点＊", Scale = JmaScale.FiveLowerOrMore };
        var quake = new QuakeEvent(original.Id, "contract-fixture", original.IssuedAt,
            original.ReceivedAt, "", SourceMode.Production, original.Issue, QuakeIssueType.DetailScale,
            original.Earthquake, [point], "");
        string signature = new EventSignatureBuilder().Build(quake);
        var program = new EEWTelop.Application.Display.PageComposer().Compose(quake,
            EEWTelop.Application.Configuration.AppSettings.CreateDefault().Display);
        string display = string.Join("\n", program.Pages.Select(p => p.AccessibleText));
        StringAssert.Contains(display, "輪島市観測点");
        Assert.DoesNotContain("輪島市観測点＊", display);
        Assert.AreEqual(signature, new EventSignatureBuilder().Build(quake));
        var state = new ExternalEarthquakeState();
        state.Observe(new EventIngestionResult(EventIngestionStatus.Accepted, quake, null, null, []));
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(state.ReadQuake()));
        JsonElement published = json.RootElement.GetProperty("earthquake").GetProperty("points")[0];
        Assert.AreEqual("輪島市観測点＊", published.GetProperty("name").GetString());
        Assert.AreEqual("5-?", published.GetProperty("intensity").GetString());
        Assert.AreEqual(point.StationCode, published.GetProperty("stationCode").GetString());
    }

    [TestMethod]
    public void TsunamiMetadataSurvivesMergeAndChangesSignature()
    {
        var now = DateTimeOffset.UtcNow;
        TsunamiEvent Create(string code, string parentCode, string headline, string comment) => new(
            EventId.Create("metadata"), "contract-fixture", now, now, "", SourceMode.Production,
            new IssueInfo("気象庁", now, "VTSE51", CorrectionType.None),
            [new TsunamiArea(TsunamiGrade.Warning, false, "宮古", null, null)
                { Role = TsunamiInformationRole.CoastalObservation, Code = code, ParentAreaCode = parentCode }],
            false, null, headline: headline, comment: comment);
        var original = Create("21001", "210", "見出し", "本文");
        var merged = new TsunamiEventStateAccumulator().Merge(original);
        Assert.AreEqual(original.Headline, merged.Headline);
        Assert.AreEqual(original.Comment, merged.Comment);
        Assert.AreEqual("21001", merged.Areas[0].Code);
        Assert.AreEqual("210", merged.Areas[0].ParentAreaCode);
        var builder = new EventSignatureBuilder();
        string signature = builder.Build(original);
        foreach (var changed in new[] { Create("21002", "210", "見出し", "本文"),
                     Create("21001", "220", "見出し", "本文"),
                     Create("21001", "210", "見出し変更", "本文"),
                     Create("21001", "210", "見出し", "本文変更") })
            Assert.AreNotEqual(signature, builder.Build(changed));
    }

    [TestMethod]
    public void SimulatorDecoderConsumesSharedEarthquakeContract()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement item = fixture.RootElement.GetProperty("earthquakeItem");
        var quake = (QuakeEvent)DisasterSimulatorDecoder.Decode("earthquake", item,
            DateTimeOffset.Parse("2026-09-26T01:00:11+09:00", CultureInfo.InvariantCulture));

        Assert.AreEqual(item.GetProperty("comment").GetString(), quake.FreeFormComment);
        Assert.AreEqual(6.7, quake.Earthquake.Hypocenter!.Magnitude);
        Assert.IsNotNull(quake.LongPeriodIntensity);
        Assert.AreEqual(4, quake.LongPeriodIntensity.MaximumClass);
        Assert.AreEqual("390", quake.Points.Single().SeismicAreaCode);
    }

    [TestMethod]
    public void SimulatorDecoderConsumesSharedTsunamiContract()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement item = fixture.RootElement.GetProperty("tsunamiObservationItem");
        var tsunami = (TsunamiEvent)DisasterSimulatorDecoder.Decode("tsunami", item,
            DateTimeOffset.Parse("2026-09-26T01:30:01+09:00", CultureInfo.InvariantCulture));

        Assert.AreEqual(item.GetProperty("headline").GetString(), tsunami.Headline);
        Assert.AreEqual(item.GetProperty("comment").GetString(), tsunami.Comment);
        TsunamiArea area = tsunami.Areas.Single();
        Assert.AreEqual("21099", area.Code);
        Assert.AreEqual("210", area.ParentAreaCode);
        Assert.AreEqual(TsunamiInformationRole.OffshoreObservation, area.Role);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-26T01:29:00+09:00", CultureInfo.InvariantCulture), tsunami.ObservationAsOf);
    }

    [TestMethod]
    public void ProductionEarthquakeApiPublishesSharedAdditiveFields()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement expected = fixture.RootElement.GetProperty("earthquakeItem");
        DateTimeOffset issued = DateTimeOffset.Parse(expected.GetProperty("issuedAt").GetString()!, CultureInfo.InvariantCulture);
        var quake = new QuakeEvent(
            EventId.Create(expected.GetProperty("eventId").GetString()!), "contract-fixture", issued, issued,
            "sig", SourceMode.Production,
            new IssueInfo("気象庁", issued, "VXSE62", CorrectionType.None, "2", "発表"),
            QuakeIssueType.LongPeriodObservation,
            new EarthquakeInfo(DateTimeOffset.Parse("2026-09-26T01:00:00+09:00", CultureInfo.InvariantCulture), null,
                new HypocenterInfo("能登半島沖", "能登半島沖", 37.5, 137.2, 10, 6.7, ""),
                JmaScale.SixLower, DomesticTsunami.None, ForeignTsunami.Unknown),
            [new QuakePoint("石川県", "輪島市鳳至町", false, JmaScale.SixLower, "輪島市鳳至町")
                { StationCode = "1720402", MunicipalityCode = "17204", MunicipalityName = "輪島市",
                  SeismicAreaCode = "390", SeismicAreaName = "石川県能登", Latitude = 37.391667, Longitude = 136.895 }],
            expected.GetProperty("comment").GetString()!,
            new LongPeriodIntensityInfo(4, [new LongPeriodIntensityArea("石川県", "石川県能登", 4)]),
            headline: expected.GetProperty("headline").GetString()!);
        var state = new ExternalEarthquakeState();
        state.Observe(new EventIngestionResult(EventIngestionStatus.Accepted, quake, null, null, []));
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(state.ReadQuake()));
        JsonElement item = actual.RootElement.GetProperty("earthquake");
        Assert.AreEqual(4, item.GetProperty("longPeriodIntensity").GetProperty("maximumClass").GetInt32());
        Assert.AreEqual("390", item.GetProperty("points")[0].GetProperty("seismicAreaCode").GetString());
        Assert.AreEqual(expected.GetProperty("comment").GetString(), item.GetProperty("comment").GetString());
    }

    [TestMethod]
    public void JmaTsunamiNormalizerAndProductionApiPreserveCodesAndComments()
    {
        const string xml = """
            <Report><Control><Title>津波警報・注意報・予報</Title><Status>通常</Status></Control><Head><ReportDateTime>2026-09-26T01:02:00+09:00</ReportDateTime><EventID>T-CONTRACT-001</EventID><InfoType>発表</InfoType><Headline><Text>津波警報を発表しました。</Text></Headline></Head><Body><Tsunami><Forecast><Item><Category><Kind><Name>津波警報</Name><Code>51</Code></Kind></Category><Area><Name>岩手県</Name><Code>210</Code></Area><Station><Name>宮古</Name><Code>21001</Code></Station></Item></Forecast></Tsunami><Comments><FreeFormComment>沿岸部や川沿いにいる人は安全な場所へ避難してください。</FreeFormComment></Comments></Body></Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        var normalized = normalizer.Normalize(new("jma-xml", xml, SourceMode.Production,
            DateTimeOffset.Parse("2026-09-26T01:02:01+09:00", CultureInfo.InvariantCulture)) { ContentFormat = RawProviderContentFormat.JmaXml });
        Assert.AreEqual(NormalizeStatus.Success, normalized.Status);
        var tsunami = (TsunamiEvent)normalized.Event!;
        Assert.AreEqual("津波警報を発表しました。", tsunami.Headline);
        Assert.AreEqual("沿岸部や川沿いにいる人は安全な場所へ避難してください。", tsunami.Comment);
        Assert.AreEqual("210", tsunami.Areas.Single(a => a.Role == TsunamiInformationRole.ForecastArea).Code);

        var state = new ExternalApiState();
        state.Observe(new EventIngestionResult(EventIngestionStatus.Accepted, tsunami, null, null, []));
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(state.Read(DateTimeOffset.Parse("2026-09-26T01:03:00+09:00", CultureInfo.InvariantCulture)), WebJsonOptions));
        JsonElement forecast = actual.RootElement.GetProperty("forecast");
        Assert.AreEqual("津波警報を発表しました。", forecast.GetProperty("headline").GetString());
        Assert.AreEqual("210", forecast.GetProperty("areas")[0].GetProperty("code").GetString());
    }

    [TestMethod]
    public void JmaTsunamiObservationNormalizerPreservesStationAndParentCodes()
    {
        const string xml = """
            <Report><Control><Title>沖合の津波観測に関する情報</Title><Status>通常</Status></Control><Head><ReportDateTime>2026-09-26T01:30:00+09:00</ReportDateTime><EventID>T-CONTRACT-001</EventID><InfoType>発表</InfoType><Headline><Text>２６日０１時２９分現在の、沖合の津波観測値をお知らせします。</Text></Headline></Head><Body><Tsunami><Observation><Item><Area><Name>岩手県</Name><Code>210</Code></Area><Station><Name>岩手沖ＧＰＳ波浪計</Name><Code>21099</Code><FirstHeight><Initial>既に津波到達と推測</Initial></FirstHeight></Station></Item></Observation></Tsunami><Comments><WarningComment><Text>今後、津波の高さはさらに高くなることがあります。</Text></WarningComment></Comments></Body></Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        var normalized = normalizer.Normalize(new("jma-xml", xml, SourceMode.Production,
            DateTimeOffset.Parse("2026-09-26T01:30:01+09:00", CultureInfo.InvariantCulture)) { ContentFormat = RawProviderContentFormat.JmaXml });
        Assert.AreEqual(NormalizeStatus.Success, normalized.Status);
        var tsunami = (TsunamiEvent)normalized.Event!;
        TsunamiArea area = tsunami.Areas.Single();
        Assert.AreEqual(TsunamiInformationRole.OffshoreObservation, area.Role);
        Assert.AreEqual("21099", area.Code);
        Assert.AreEqual("210", area.ParentAreaCode);
        Assert.AreEqual("岩手県", area.ParentAreaName);
        Assert.AreEqual("今後、津波の高さはさらに高くなることがあります。", tsunami.Comment);

        var state = new ExternalApiState();
        state.Observe(new EventIngestionResult(EventIngestionStatus.Accepted, tsunami, null, null, []));
        using JsonDocument actual = JsonDocument.Parse(JsonSerializer.Serialize(
            state.Read(DateTimeOffset.Parse("2026-09-26T01:31:00+09:00", CultureInfo.InvariantCulture)),
            WebJsonOptions));
        JsonElement observation = actual.RootElement.GetProperty("observation");
        JsonElement publishedArea = observation.GetProperty("areas")[0];
        Assert.AreEqual("21099", publishedArea.GetProperty("code").GetString());
        Assert.AreEqual("210", publishedArea.GetProperty("parentAreaCode").GetString());
    }

}
