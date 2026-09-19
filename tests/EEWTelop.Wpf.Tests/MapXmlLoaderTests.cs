using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class MapXmlLoaderTests
{
    private const string Xml = """
        <Report><Control><Title>震源・震度に関する情報</Title><Status>通常</Status></Control>
        <Head><ReportDateTime>2026-09-19T00:00:00+09:00</ReportDateTime><EventID>map-test</EventID><InfoType>発表</InfoType></Head>
        <Body><Intensity><Observation><MaxInt>4</MaxInt><Pref><Name>宮崎県</Name><Area><Name>宮崎県北部平野部</Name><Code>851</Code>
        <City><Name>都農町</Name><Code>4540600</Code><IntensityStation><Name>都農町役場</Name><Code>station-a</Code><Int>4</Int></IntensityStation>
        <IntensityStation><Name>都農町別地点</Name><Code>station-b</Code><Int>2</Int></IntensityStation></City>
        </Area></Pref></Observation></Intensity></Body></Report>
        """;

    [TestMethod]
    public async Task LocalXmlRetainsStationsButMapsMunicipalityMaximumAndTrainingStatus()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            await File.WriteAllTextAsync(path, Xml);
            var quake = await MapXmlLoader.LoadAsync(path);
            Assert.AreEqual(SourceMode.HistoryRehearsal, quake.SourceMode);
            Assert.HasCount(2, quake.Points);
            Assert.AreEqual("station-a", quake.Points[0].StationCode);
            var rows = TrialQuakeMap.GetRegionRows(quake);
            Assert.HasCount(1, rows);
            Assert.AreEqual("city:4540600", rows[0].Code);
            Assert.AreEqual(JmaScale.Four, rows[0].Scale);
            Assert.IsTrue(rows[0].Name.Contains("市町村代表点", StringComparison.Ordinal));
            await File.WriteAllTextAsync(path, Xml.Replace("通常", "訓練"));
            Assert.AreEqual(SourceMode.ManualTest, (await MapXmlLoader.LoadAsync(path)).SourceMode);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task RejectsUnsupportedMalformedAndExternalEntityXml()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
        try
        {
            foreach (string xml in new[] { "<Report>", "<Other/>", "<Report><Control><Title>未対応</Title></Control></Report>",
                "<!DOCTYPE Report [<!ENTITY x SYSTEM 'file:///nonexistent'>]><Report>&x;</Report>", new string('x', 2 * 1024 * 1024 + 1) })
            {
                await File.WriteAllTextAsync(path, xml);
                bool rejected = false;
                try { await MapXmlLoader.LoadAsync(path); }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { rejected = true; }
                Assert.IsTrue(rejected);
            }
        }
        finally { File.Delete(path); }
    }
}
