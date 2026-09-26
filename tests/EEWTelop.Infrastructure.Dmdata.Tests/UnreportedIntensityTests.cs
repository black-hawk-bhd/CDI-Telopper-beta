using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class UnreportedIntensityTests
{
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    public void AreaConditionUsesMostDetailedUnreportedEntry(
        bool cityCondition, bool stationCondition, bool expectedArea)
    {
        string city = cityCondition ? "<Condition>震度５弱以上未入電</Condition>" : "";
        string station = stationCondition ? "震度５弱以上未入電" : "4";
        string xml = $$"""
            <Report><Control><Title>震源・震度に関する情報</Title><Status>通常</Status></Control>
            <Head><ReportDateTime>2026-09-26T15:00:00+09:00</ReportDateTime><EventID>condition</EventID></Head>
            <Body><Intensity><Observation><MaxInt>5+</MaxInt><Pref><Name>石川県</Name>
            <Area><Name>石川県能登</Name><Code>390</Code><Condition>震度５弱以上未入電</Condition>
            <City><Name>輪島市</Name><Code>1720400</Code>{{city}}
            <IntensityStation><Name>輪島市観測点＊</Name><Code>1720431</Code><Int>{{station}}</Int></IntensityStation>
            </City></Area></Pref></Observation></Intensity></Body></Report>
            """;
        foreach (string provider in new[] { "jma-xml", "dmdata.jp", "axis" })
        {
            var result = new JmaXmlEventNormalizer(new EventSignatureBuilder()).Normalize(
                new RawProviderMessage(provider, xml, SourceMode.Production, DateTimeOffset.UtcNow)
                { ContentFormat = RawProviderContentFormat.JmaXml });
            Assert.IsTrue(result.IsSuccess, provider);
            QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
            QuakePoint point = quake.Points.Single(p => p.Scale == JmaScale.FiveLowerOrMore);
            Assert.AreEqual(expectedArea, point.IsArea);
            Assert.AreEqual("390", point.SeismicAreaCode);
            Assert.AreEqual(stationCondition ? "1720431" : "", point.StationCode);
            Assert.AreEqual(stationCondition ? 1 : 2, quake.Points.Count);
        }
    }
}
