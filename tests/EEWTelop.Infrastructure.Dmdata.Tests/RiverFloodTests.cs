using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class RiverFloodTests
{
    [TestMethod]
    public void ArchivedOfficialSamplesWhenExplicitlyProvided()
    {
        // Optional local corpus stays outside the published repository and build dependencies.
        string? directory = Environment.GetEnvironmentVariable("EEWTELOP_FLOOD_SAMPLES");
        if (string.IsNullOrWhiteSpace(directory)) return;
        string[] files = Directory.GetFiles(directory, "*VXKO*.xml");
        Assert.IsGreaterThan(0, files.Length);
        foreach (string file in files)
        {
            string xml = File.ReadAllText(file);
            WeatherWarningEvent weather = Weather(xml, "test-library-jma-xml");
            Assert.IsGreaterThan(0, Compose(weather).Pages.Count, file);
            Assert.IsNotNull(weather.RiverFlood, file);
            Assert.IsGreaterThan(0, weather.RiverFlood.AlertLevel, file);
            var document = System.Xml.Linq.XDocument.Parse(xml);
            string[] mainTexts = document.Descendants().Where(e => e.Name.LocalName == "Property" &&
                    e.Elements().Any(t => t.Name.LocalName == "Type" && t.Value == "主文"))
                .SelectMany(e => e.Elements().Where(t => t.Name.LocalName == "Text"))
                .Select(e => e.Value.Trim()).Where(s => s.Length > 0).Distinct().ToArray();
            CollectionAssert.AreEqual(mainTexts, weather.RiverFlood.MainTexts.ToArray(), file);
            if (!weather.IsCancelled && weather.RiverFlood.AlertLevel >= 4)
            {
                string text = AllText(Compose(weather));
                foreach (string main in mainTexts)
                    StringAssert.Contains(string.Concat(text.Where(c => !char.IsWhiteSpace(c))),
                        string.Concat(main.Where(c => !char.IsWhiteSpace(c))), file);
            }
        }
    }

    private static string Xml(string code = "41", string type = "VXKO76", string info = "発表",
        string status = "通常") => $$"""
        <Report xmlns="http://xml.kishou.go.jp/jmaxml1/">
          <Control><Title>指定河川洪水予報</Title><Type>{{type}}</Type><Status>{{status}}</Status></Control>
          <Head xmlns="http://xml.kishou.go.jp/jmaxml1/informationBasis1/">
            <Title>善福寺川氾濫危険情報</Title><EventID>830304004900</EventID>
            <ReportDateTime>2026-09-06T23:51:00+09:00</ReportDateTime><InfoType>{{info}}</InfoType><Serial>2</Serial>
            <Headline><Text>善福寺川今後氾濫するおそれ</Text>
              <Information type="指定河川洪水予報（予報区域）"><Item>
                <Kind><Name>氾濫危険情報</Name><Code>{{code}}</Code></Kind>
                <Areas><Area><Name>善福寺川</Name><Code>830304004900</Code></Area></Areas>
              </Item></Information>
              <Information type="指定河川洪水予報（府県予報区等）"><Item><Areas>
                <Area><Name>東京都</Name><Code>130000</Code></Area>
                <Area><Name>埼玉県</Name><Code>110000</Code></Area>
              </Areas></Item></Information>
            </Headline>
          </Head>
          <Body xmlns="http://xml.kishou.go.jp/jmaxml1/body/meteorology1/">
            <Warning type="指定河川洪水予報">
              <Item><Kind><Property><Type>主文</Type><Text>これは合成テストの主文です。市町村からの避難情報を確認するとともに、各自安全確保を図るなど、適切な避難行動をとってください。</Text></Property></Kind></Item>
              <Item><Kind><Property><Type>浸水想定地区</Type><Text>地区案内</Text></Property></Kind><Areas>
                <Area><Name>白山前橋</Name><Prefecture>東京都</Prefecture><PrefectureCode>13</PrefectureCode><City>中野区</City><CityCode>13114</CityCode></Area>
                <Area><Name>白山前橋</Name><Prefecture>東京都</Prefecture><PrefectureCode>13</PrefectureCode><City>杉並区</City><CityCode>13115</CityCode></Area>
                <Area><Name>松見橋</Name><Prefecture>東京都</Prefecture><PrefectureCode>13</PrefectureCode><City>中野区</City><CityCode>13114</CityCode></Area>
                <Area><Name>松見橋</Name><Prefecture>東京都</Prefecture><PrefectureCode>13</PrefectureCode><City>杉並区</City><CityCode>13115</CityCode></Area>
                <Area><Name>試験橋</Name><Prefecture>埼玉県</Prefecture><PrefectureCode>11</PrefectureCode><City>試験市</City><SubCityList>試験地区</SubCityList></Area>
              </Areas></Item>
            </Warning>
          </Body>
        </Report>
        """;

    private static NormalizeResult Normalize(string xml, string provider = "dmdata.jp") =>
        new JmaXmlEventNormalizer(new EventSignatureBuilder()).Normalize(new RawProviderMessage(
            provider, xml, SourceMode.Production, DateTimeOffset.UtcNow)
        { ContentFormat = RawProviderContentFormat.JmaXml });

    private static WeatherWarningEvent Weather(string xml, string provider = "dmdata.jp") =>
        Assert.IsInstanceOfType<WeatherWarningEvent>(Normalize(xml, provider).Event);

    private static DisplayProgram Compose(WeatherWarningEvent weather) =>
        new PageComposer().Compose(weather, AppSettings.CreateDefault().Display with { ShowPageIndicator = false });

    private static string AllText(DisplayProgram program) => string.Join("", program.Pages
        .SelectMany(p => p.Blocks).Select(b => b.PrimaryText));

    [TestMethod]
    [DataRow("10", 2, true)]
    [DataRow("20", 2, false)]
    [DataRow("21", 2, false)]
    [DataRow("22", 2, false)]
    [DataRow("30", 3, false)]
    [DataRow("31", 3, false)]
    [DataRow("40", 4, false)]
    [DataRow("41", 4, false)]
    [DataRow("51", 5, false)]
    [DataRow("53", 5, false)]
    public void WarningCodesDistinguishDowngradeFromRelease(string code, int level, bool cancelled)
    {
        WeatherWarningEvent weather = Weather(Xml(code));
        Assert.AreEqual(WeatherInformationType.RiverFlood, weather.InformationType);
        Assert.AreEqual(level, weather.RiverFlood!.AlertLevel);
        Assert.AreEqual(cancelled, weather.IsCancelled);
        Assert.AreEqual(!cancelled, weather.Items.All(i => i.IsActive));
        Assert.AreEqual(level >= 4, AllText(Compose(weather)).Contains("合成テストの主文"));
    }

    [TestMethod]
    public void DmdataAxisAndLocalSupportAllFortyTypesButNotAdjacentCodes()
    {
        foreach (string provider in new[] { "dmdata.jp", "axis", "test-library-jma-xml" })
        {
            foreach (int code in Enumerable.Range(50, 40))
                Assert.AreEqual($"VXKO{code}", Weather(Xml(type: $"VXKO{code}"), provider).Issue.RawType);
            Assert.AreEqual(NormalizeStatus.Ignored, Normalize(Xml(type: "VXKO49"), provider).Status);
            Assert.AreEqual(NormalizeStatus.Ignored, Normalize(Xml(type: "VXKO90"), provider).Status);
        }
        Assert.AreEqual("VXKO", Weather(Xml(type: "")).Issue.RawType);
    }

    [TestMethod]
    public void LayoutGroupsStationsAndAppendsTwoLineMainTextPages()
    {
        DisplayProgram program = Compose(Weather(Xml()));
        Assert.AreEqual("善福寺川今後氾濫するおそれ", program.Pages[0].Blocks[0].PrimaryText);
        StringAssert.Contains(program.Pages[1].Blocks[0].PrimaryText, "浸水が想定される地区");
        string text = AllText(program);
        StringAssert.Contains(text, "白山前橋　東京都　中野区　杉並区");
        StringAssert.Contains(text, "松見橋　東京都　中野区　杉並区");
        StringAssert.Contains(text, "適切な避難行動をとってください。");
        foreach (var page in program.Pages)
        {
            Assert.IsLessThanOrEqualTo(2, page.Blocks.Count);
            Assert.IsLessThanOrEqualTo(48, page.Blocks.Sum(b => b.PrimaryText.Length));
        }
    }

    [TestMethod]
    public void FilteringRetainsStructuredDataAndLimitsDistricts()
    {
        WeatherWarningEvent weather = Weather(Xml());
        WeatherWarningEvent tokyo = weather.WithItems(weather.Items.Where(i => i.AreaName == "東京都").ToArray());
        Assert.IsNotNull(tokyo.RiverFlood);
        string text = AllText(Compose(tokyo));
        StringAssert.Contains(text, "中野区");
        Assert.IsFalse(text.Contains("試験市"));
        Assert.IsNull(EventDisplayFilter.Apply(AppSettings.CreateDefault().Filter with
            { WeatherWarning = false }, weather));
        Assert.IsNull(EventDisplayFilter.Apply(AppSettings.CreateDefault().Filter with
            { WeatherAdvisories = false }, Weather(Xml("21"))));
    }

    [TestMethod]
    public void CancellationMissingBodyAndTrainingAreHandledSafely()
    {
        string xml = Xml(info: "取消");
        xml = xml[..xml.IndexOf("<Body", StringComparison.Ordinal)] + "</Report>";
        WeatherWarningEvent cancelled = Weather(xml);
        Assert.IsTrue(cancelled.IsCancelled);
        StringAssert.Contains(AllText(Compose(cancelled)), "善福寺川");
        Assert.AreEqual(1, Compose(cancelled).Pages.Count);
        Assert.AreNotEqual(SourceMode.Production, Weather(Xml(status: "訓練")).SourceMode);
        Assert.AreNotEqual(SourceMode.Production, Weather(Xml(status: "試験")).SourceMode);
    }

    [TestMethod]
    public void MainTextChangesAffectSignatureAndRiverIdsAreStable()
    {
        WeatherWarningEvent original = Weather(Xml());
        WeatherWarningEvent changed = Weather(Xml().Replace("合成テストの主文", "変更後の主文"));
        Assert.AreNotEqual(original.Signature, changed.Signature);
        Assert.AreEqual(original.Id, changed.Id);
    }

    [TestMethod]
    public void SubscriptionAddsFloodOnlyWhenWeatherReceptionIsEnabled()
    {
        var options = new DmdataProviderOptions(new Uri("https://api.dmdata.jp/"), "",
            DmdataAuthenticationMode.ApiKey, false);
        Assert.IsFalse(options.TelegramTypes.Any(t => t.StartsWith("VXKO", StringComparison.Ordinal)));
        foreach (bool legacy in new[] { false, true })
        {
            var enabled = options with { ReceiveWeatherWarnings = true, UseLegacyWeatherWarningTelegrams = legacy };
            Assert.AreEqual(40, enabled.TelegramTypes.Count(t => t.StartsWith("VXKO", StringComparison.Ordinal)));
        }
    }
}
