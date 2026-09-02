using System;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Wolfx.Configuration;
using EEWTelop.Infrastructure.Wolfx.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Wolfx.Tests;

[TestClass]
public sealed class WolfxEventNormalizerTests
{
    private readonly WolfxEventNormalizer _normalizer = new(new StubSignatureBuilder());

    [TestMethod]
    public void WarningEewNormalizesWolfxFieldsAndAreas()
    {
        const string json = """
            {
              "type":"jma_eew",
              "Title":"緊急地震速報（警報）",
              "Issue":{"Source":"東京","Status":"通常"},
              "EventID":"20260902102707",
              "Serial":4,
              "AnnouncedTime":"2026/09/02 10:27:58",
              "OriginTime":"2026/09/02 10:27:02",
              "Hypocenter":"宮古島近海",
              "Latitude":24.9,
              "Longitude":125.1,
              "Magunitude":6.2,
              "Depth":30,
              "MaxIntensity":"6-",
              "WarnArea":[{"Chiiki":"沖縄本島北部","Shindo1":"5弱","Shindo2":"6弱","Arrive":false}],
              "isTraining":false,
              "isAssumption":false,
              "isWarn":true,
              "isFinal":true,
              "isCancel":false
            }
            """;

        NormalizeResult result = _normalizer.Normalize(Raw(json));

        Assert.IsTrue(result.IsSuccess);
        var eew = (EewEvent)result.Event!;
        Assert.AreEqual("20260902102707", eew.Id.Value);
        Assert.AreEqual(JmaScale.SixLower, eew.Earthquake!.MaximumScale);
        Assert.AreEqual(6.2, eew.Earthquake.Hypocenter!.Magnitude);
        Assert.AreEqual(TimeSpan.FromHours(9), eew.IssuedAt.Offset);
        Assert.HasCount(1, eew.Areas);
        Assert.AreEqual("沖縄本島北部", eew.Areas[0].Name);
        Assert.AreEqual(JmaScale.FiveLower, eew.Areas[0].ScaleFrom);
        Assert.AreEqual((int)JmaScale.SixLower, eew.Areas[0].ScaleTo);
        Assert.IsTrue(eew.IsFinal);
        Assert.AreEqual("signature", eew.Signature);
    }

    [TestMethod]
    public void ForecastOnlyEewIsIgnored()
    {
        const string json = """
            {"type":"jma_eew","EventID":"1","isWarn":false,"isCancel":false,"WarnArea":[]}
            """;

        NormalizeResult result = _normalizer.Normalize(Raw(json));

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
    }

    [TestMethod]
    public void LatestEarthquakeListEntryNormalizesAsQuake()
    {
        const string json = """
            {
              "type":"jma_eqlist",
              "md5":"test",
              "No1":{
                "Title":"震源・震度情報",
                "EventID":"20260902225039",
                "time":"2026/09/02 22:50",
                "time_full":"2026/09/02 22:50:00",
                "location":"陸奥湾",
                "magnitude":"3.0",
                "shindo":"2",
                "depth":"10km",
                "latitude":"41.1",
                "longitude":"140.8",
                "info":"この地震による津波の心配はありません。"
              }
            }
            """;

        NormalizeResult result = _normalizer.Normalize(Raw(json));

        Assert.IsTrue(result.IsSuccess);
        var quake = (QuakeEvent)result.Event!;
        Assert.AreEqual("20260902225039", quake.Id.Value);
        Assert.AreEqual(QuakeIssueType.DetailScale, quake.IssueType);
        Assert.AreEqual("陸奥湾", quake.Earthquake.Hypocenter!.Name);
        Assert.AreEqual(10, quake.Earthquake.Hypocenter.DepthKilometers);
        Assert.AreEqual(JmaScale.Two, quake.Earthquake.MaximumScale);
        Assert.AreEqual(DomesticTsunami.None, quake.Earthquake.DomesticTsunami);
        Assert.HasCount(0, quake.Points);
    }

    [TestMethod]
    public void HeartbeatIsIgnored()
    {
        NormalizeResult result = _normalizer.Normalize(Raw("{\"type\":\"heartbeat\"}"));

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
    }

    [TestMethod]
    public void OptionsConnectOnlySelectedWolfxCategories()
    {
        ProviderSettings provider = AppSettings.CreateDefault().Provider with
        {
            Routing = AppSettings.CreateDefault().Provider.Routing with
            {
                Eew = ReceptionProvider.Wolfx,
                Quake = ReceptionProvider.P2pQuake,
            },
        };

        WolfxProviderOptions options = WolfxProviderOptions.FromSettings(provider);

        Assert.IsTrue(options.ReceiveEew);
        Assert.IsFalse(options.ReceiveQuake);
        Assert.AreEqual("wss://ws-api.wolfx.jp/jma_eew", options.EewWebSocketUri.AbsoluteUri);
        Assert.HasCount(0, options.Validate());
    }

    private static RawProviderMessage Raw(string json) => new(
        WolfxProviderOptions.ProviderName,
        json,
        SourceMode.Production,
        new DateTimeOffset(2026, 9, 2, 13, 30, 0, TimeSpan.Zero));

    private sealed class StubSignatureBuilder : IEventSignatureBuilder
    {
        public string Build(DisasterEvent disasterEvent)
        {
            ArgumentNullException.ThrowIfNull(disasterEvent);
            return "signature";
        }
    }
}
