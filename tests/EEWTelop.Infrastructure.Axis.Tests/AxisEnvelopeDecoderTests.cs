using System.Globalization;
using System.Xml.Linq;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Normalization;
using EEWTelop.Infrastructure.Axis.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisEnvelopeDecoderTests
{
    [TestMethod]
    public void PlainTextControlFramesAreRecognized()
    {
        Assert.AreEqual(
            AxisFrameKind.Hello,
            AxisEnvelopeDecoder.Decode("hello", "jmx-seismology").Kind);
        Assert.AreEqual(
            AxisFrameKind.Heartbeat,
            AxisEnvelopeDecoder.Decode("hb", "jmx-seismology").Kind);
    }

    [TestMethod]
    public void DecoderReportsJmaTelegramTypeForSelectionPolicy()
    {
        const string json = """
            {
              "channel": "jmx-meteorology",
              "message": {
                "Report": {
                  "Control": { "Title": "気象警報・注意報", "Status": "通常", "Type": "VPWW55" },
                  "Head": { "EventID": "weather-test", "InfoType": "発表" },
                  "Body": {}
                }
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-meteorology");

        Assert.AreEqual(AxisFrameKind.Data, frame.Kind);
        Assert.AreEqual("VPWW55", frame.TelegramType);
    }

    [TestMethod]
    public void Vxse45AxisFrameNormalizesOnlyWarningAreas()
    {
        const string json = """
            {
              "channel": "jmx-seismology",
              "message": {
                "Report": {
                  "Control": {
                    "Title": "緊急地震速報（地震動予報）",
                    "Status": "通常",
                    "Type": "VXSE45",
                    "PublishingOffice": "気象庁"
                  },
                  "Head": {
                    "Title": "緊急地震速報（地震動予報）",
                    "ReportDateTime": "2024-04-17T23:14:59+09:00",
                    "EventID": "20240417231454",
                    "InfoType": "発表",
                    "Serial": "4",
                    "Headline": {
                      "Text": "強い揺れ",
                      "Information": {
                        "Item": {
                          "Kind": { "Name": "緊急地震速報（警報）", "Code": "31" }
                        }
                      }
                    }
                  },
                  "Body": {
                    "Intensity": {
                      "Forecast": {
                        "Pref": [{
                          "Name": "愛媛",
                          "Area": [
                            {
                              "Name": "愛媛県南予",
                              "Category": { "Kind": { "Name": "緊急地震速報（警報）", "Code": "10" } },
                              "ForecastInt": { "From": "5-", "To": "5-" }
                            },
                            {
                              "Name": "愛媛県東予",
                              "Category": { "Kind": { "Name": "緊急地震速報（予報）", "Code": "01" } },
                              "ForecastInt": { "From": "3", "To": "4" }
                            }
                          ]
                        }]
                      }
                    }
                  },
                  "uuid_": "20240417141459_0_VXSE45_010000"
                }
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(
            json,
            AxisProviderOptions.SeismologyChannel);
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.Xml!,
            SourceMode.Production,
            new DateTimeOffset(2024, 4, 17, 14, 14, 59, TimeSpan.Zero))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(AxisFrameKind.Data, frame.Kind);
        Assert.AreEqual("VXSE45", frame.TelegramType);
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            frame.Channel,
            frame.TelegramType));
        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.IsTrue(eew.IsWarning);
        Assert.HasCount(1, eew.Areas);
        Assert.AreEqual("愛媛県南予", eew.Areas[0].Name);
        Assert.AreEqual(EewWarningKind.ForecastNotArrived, eew.Areas[0].WarningKind);
    }

    [TestMethod]
    public void DedicatedEewChannelNormalizesWarningJson()
    {
        const string json = """
            {
              "channel": "eew",
              "message": {
                "Title": "緊急地震速報（警報）",
                "OriginDateTime": "2024-04-17T23:14:48+09:00",
                "ReportDateTime": "2024-04-17T23:14:59+09:00",
                "EventID": "20240417231454",
                "Serial": 4,
                "Hypocenter": {
                  "Code": 681,
                  "Name": "豊後水道",
                  "Coordinate": [33.2, 132.4],
                  "Depth": "30km",
                  "Description": "北緯33.2度 東経132.4度"
                },
                "Intensity": "5-",
                "Magnitude": "5.8",
                "Flag": {
                  "is_final": false,
                  "is_cancel": false,
                  "is_training": false
                },
                "Forecast": [
                  {
                    "Code": 622,
                    "Name": "愛媛県南予",
                    "Intensity": { "From": "5-", "To": "5-", "Description": "" }
                  },
                  {
                    "Code": 620,
                    "Name": "愛媛県東予",
                    "Intensity": { "From": "3", "To": "3", "Description": "" }
                  }
                ],
                "Text": "強い揺れに警戒してください。"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(
            json,
            AxisProviderOptions.DefaultChannel);
        var signatureBuilder = new EventSignatureBuilder();
        var normalizer = new AxisEventNormalizer(
            new JmaXmlEventNormalizer(signatureBuilder),
            signatureBuilder);
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.ProviderPayload!,
            SourceMode.Production,
            DateTimeOffset.Parse("2024-04-17T14:15:00Z", CultureInfo.InvariantCulture))
        {
            ContentFormat = frame.ContentFormat,
            TransportPayload = json,
            TransportContentFormat = RawProviderContentFormat.Json,
        });

        Assert.AreEqual(AxisFrameKind.Data, frame.Kind);
        Assert.AreEqual(AxisProviderOptions.EewChannel, frame.Channel);
        Assert.AreEqual("AXIS-EEW", frame.TelegramType);
        Assert.AreEqual(RawProviderContentFormat.Json, frame.ContentFormat);
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            frame.Channel,
            frame.TelegramType));
        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.AreEqual("20240417231454", eew.Id.Value);
        Assert.AreEqual("4", eew.Issue.Serial);
        Assert.AreEqual("AXIS-EEW", eew.Issue.RawType);
        Assert.IsTrue(eew.IsWarning);
        Assert.HasCount(1, eew.Areas);
        Assert.AreEqual("愛媛県南予", eew.Areas[0].Name);
        Assert.AreEqual(JmaScale.FiveLower, eew.Areas[0].ScaleFrom);
        Assert.IsNotNull(eew.Earthquake?.Hypocenter);
        Assert.AreEqual("豊後水道", eew.Earthquake.Hypocenter.Name);
        Assert.AreEqual(30, eew.Earthquake.Hypocenter.DepthKilometers);
        Assert.AreEqual(5.8, eew.Earthquake.Hypocenter.Magnitude);

        DisplayProgram program = new PageComposer().Compose(
            eew,
            AppSettings.CreateDefault().Display);
        Assert.IsNotEmpty(program.Pages);
    }

    [TestMethod]
    public void DedicatedEewChannelIgnoresForecastOnlyJson()
    {
        const string json = """
            {
              "channel": "eew",
              "message": {
                "Title": "緊急地震速報（予報）",
                "OriginDateTime": "2024-04-17T23:14:48+09:00",
                "ReportDateTime": "2024-04-17T23:14:50+09:00",
                "EventID": "20240417231454",
                "Serial": 1,
                "Intensity": "4",
                "Magnitude": "5.0",
                "Flag": { "is_final": false, "is_cancel": false, "is_training": false },
                "Forecast": [],
                "Text": "緊急地震速報を発表しました。"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, AxisProviderOptions.DefaultChannel);
        var signatureBuilder = new EventSignatureBuilder();
        var normalizer = new AxisEventNormalizer(
            new JmaXmlEventNormalizer(signatureBuilder),
            signatureBuilder);
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.ProviderPayload!,
            SourceMode.Production,
            DateTimeOffset.UtcNow)
        {
            ContentFormat = frame.ContentFormat,
        });

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
    }

    [TestMethod]
    public void DedicatedEewChannelAcceptsCancellationWithoutForecastAreas()
    {
        const string json = """
            {
              "channel": "eew",
              "message": {
                "Title": "緊急地震速報（取消）",
                "ReportDateTime": "2024-04-17T23:15:20+09:00",
                "EventID": "20240417231454",
                "Serial": 4,
                "Flag": { "is_final": true, "is_cancel": true, "is_training": false },
                "Forecast": [],
                "Text": "先ほどの緊急地震速報を取り消します。"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, AxisProviderOptions.DefaultChannel);
        var signatureBuilder = new EventSignatureBuilder();
        var normalizer = new AxisEventNormalizer(
            new JmaXmlEventNormalizer(signatureBuilder),
            signatureBuilder);
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.ProviderPayload!,
            SourceMode.Production,
            DateTimeOffset.UtcNow)
        {
            ContentFormat = frame.ContentFormat,
        });

        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.IsTrue(eew.IsCancelled);
        Assert.IsTrue(eew.IsFinal);
        Assert.IsEmpty(eew.Areas);
    }

    [TestMethod]
    public void DecoderUsesReportUuidWhenBodyContainsAnUnrelatedTypeElement()
    {
        const string json = """
            {
              "channel": "jmx-meteorology",
              "message": {
                "Report": {
                  "uuid_": "20260812080411_0_VPWW54_130000",
                  "Control": { "Status": "normal" },
                  "Head": { "EventID": "weather-test", "InfoType": "issue" },
                  "Body": {
                    "Warning": { "Type": "hazard level" }
                  }
                }
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-meteorology");

        Assert.AreEqual(AxisFrameKind.Data, frame.Kind);
        Assert.AreEqual("VPWW54", frame.TelegramType);
        Assert.IsFalse(AxisWeatherTelegramPolicy.ShouldAccept(frame.TelegramType));
    }

    [TestMethod]
    public void JmaJsonIsReconstructedAsNormalizerCompatibleXml()
    {
        const string json = """
            {
              "channel": "jmx-seismology",
              "message": {
                "Report": {
                  "Control": {
                    "Title": "震度速報",
                    "Status": "通常"
                  },
                  "Head": {
                    "ReportDateTime": "2026-08-10T12:00:00+09:00",
                    "EventID": "20260810120000",
                    "Serial": "1",
                    "InfoType": "発表"
                  },
                  "Body": {
                    "Intensity": {
                      "Observation": {
                        "MaxInt": "4"
                      }
                    }
                  }
                }
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-seismology");

        Assert.AreEqual(AxisFrameKind.Data, frame.Kind);
        Assert.IsFalse(frame.IsTest);
        XDocument xml = XDocument.Parse(frame.Xml!);
        Assert.AreEqual("震度速報", xml.Descendants("Title").Single().Value);
        Assert.AreEqual("4", xml.Descendants("MaxInt").Single().Value);
    }

    [TestMethod]
    public void AttributesAndValueOfAreConvertedWithoutNamespaceDependency()
    {
        const string json = """
            {
              "channel": "jmx-seismology",
              "message": {
                "Report": {
                  "Control": { "Title": "震源に関する情報", "Status": "訓練" },
                  "Head": { "EventID": "test", "Serial": "1", "InfoType": "発表" },
                  "Body": {
                    "Earthquake": {
                      "jmx_eb:Magnitude": { "type_": "Mj", "valueOf_": "5.0" }
                    }
                  }
                }
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-seismology");
        XElement magnitude = XDocument.Parse(frame.Xml!).Descendants("Magnitude").Single();

        Assert.AreEqual("5.0", magnitude.Value);
        Assert.AreEqual("Mj", magnitude.Attribute("type")?.Value);
        Assert.IsTrue(frame.IsTest);
    }

    [TestMethod]
    public void AxisEarthquakeMetadataDoesNotHideMagnitudeOrCoordinateValues()
    {
        const string json = """
            {
              "channel": "jmx-seismology",
              "message": {
                "Control": {
                  "Title": "震源に関する情報",
                  "Status": "通常",
                  "PublishingOffice": "気象庁"
                },
                "Head": {
                  "EventID": "20260812130555",
                  "ReportDateTime": "2026-08-12T13:08:00+09:00",
                  "TargetDateTime": "2026-08-12T13:05:00+09:00",
                  "InfoType": "発表"
                },
                "Body": {
                  "Earthquake": [{
                    "OriginTime": "2026-08-12T13:05:00+09:00",
                    "Hypocenter": {
                      "Area": {
                        "Name": "熊本県天草・芦北地方",
                        "Coordinate": [{
                          "valueOf_": "+32.5+130.5-10000/",
                          "description": "北緯３２．５度　東経１３０．５度　深さ１０ｋｍ"
                        }]
                      }
                    },
                    "Magnitude": [{
                      "type_": "Mj",
                      "valueOf_": "4.9",
                      "description": "Ｍ４．９"
                    }]
                  }]
                },
                "uuid_": "20260812040825_0_VXSE52_270000"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-seismology");
        XDocument converted = XDocument.Parse(frame.Xml!);
        XElement convertedMagnitude = converted.Descendants("Magnitude").Single();
        XElement convertedCoordinate = converted.Descendants("Coordinate").Single();
        Assert.AreEqual("4.9", convertedMagnitude.Value);
        Assert.AreEqual("Ｍ４．９", convertedMagnitude.Attribute("description")?.Value);
        Assert.AreEqual("+32.5+130.5-10000/", convertedCoordinate.Value);
        Assert.IsNotNull(convertedCoordinate.Attribute("description"));

        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            frame.Xml!,
            SourceMode.Production,
            DateTimeOffset.Parse(
                "2026-08-12T04:08:25Z",
                CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.Destination, quake.IssueType);
        Assert.IsNotNull(quake.Earthquake.Hypocenter);
        Assert.AreEqual(4.9, quake.Earthquake.Hypocenter.Magnitude);
        Assert.AreEqual(32.5, quake.Earthquake.Hypocenter.Latitude);
        Assert.AreEqual(130.5, quake.Earthquake.Hypocenter.Longitude);
        Assert.AreEqual(10, quake.Earthquake.Hypocenter.DepthKilometers);

        DisplayProgram program = new PageComposer().Compose(
            quake,
            AppSettings.CreateDefault().Display);
        Assert.IsTrue(program.Pages.Any(page =>
            page.AccessibleText.Contains("4.9", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void OtherChannelsAreIgnored()
    {
        const string json = """{"channel":"other","message":{}}""";
        Assert.AreEqual(
            AxisFrameKind.Ignored,
            AxisEnvelopeDecoder.Decode(json, "jmx-seismology").Kind);
    }

    [TestMethod]
    public void OfficialMessageShapeWithoutReportWrapperIsAccepted()
    {
        const string json = """
            {
              "channel": "jmx-seismology",
              "message": {
                "uuid_": "test-message",
                "Control": { "Title": "震度速報", "Status": "通常" },
                "Head": { "EventID": "event-1", "Serial": "1", "InfoType": "発表" },
                "Body": {}
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-seismology");
        XElement report = XDocument.Parse(frame.Xml!).Root!;

        Assert.AreEqual("Report", report.Name.LocalName);
        Assert.AreEqual("test-message", report.Attribute("uuid")?.Value);
        Assert.AreEqual("震度速報", report.Descendants("Title").Single().Value);
    }

    [TestMethod]
    public void JmxSeismologySampleShapePassesThroughExistingNormalizer()
    {
        const string json = """
            {
              "channel": "jmx-seismology",
              "message": {
                "Control": {
                  "Status": "通常",
                  "PublishingOffice": "気象庁",
                  "Title": "震度速報"
                },
                "Body": {
                  "Intensity": {
                    "Observation": {
                      "MaxInt": "5+",
                      "Pref": [{
                        "Code": "04",
                        "MaxInt": "5+",
                        "Name": "宮城県",
                        "Area": [{
                          "City": [],
                          "Code": "220",
                          "MaxInt": "5+",
                          "Name": "宮城県北部"
                        }]
                      }]
                    }
                  }
                },
                "Head": {
                  "EventID": "20210320180954",
                  "TargetDateTime": "2021-03-20T18:09:00+09:00",
                  "InfoType": "発表",
                  "Title": "震度速報",
                  "ReportDateTime": "2021-03-20T18:11:00+09:00",
                  "Serial": ""
                },
                "uuid_": "20210320091132_0_VXSE51_010000"
              }
            }
            """;
        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(json, "jmx-seismology");
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "axis",
            frame.Xml!,
            SourceMode.Production,
            DateTimeOffset.Parse(
                "2021-03-20T09:11:32Z",
                CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.IsTrue(result.IsSuccess);
        QuakeEvent quake = Assert.IsInstanceOfType<QuakeEvent>(result.Event);
        Assert.AreEqual(QuakeIssueType.ScalePrompt, quake.IssueType);
        Assert.AreEqual(JmaScale.FiveUpper, quake.Earthquake.MaximumScale);
        Assert.IsTrue(quake.Points.Any(point => point.Address == "宮城県北部"));
    }

    [TestMethod]
    public void JmxMeteorologyWarningUsesSharedJmaNormalizerAndPageComposer()
    {
        const string json = """
            {
              "channel": "jmx-meteorology",
              "message": {
                "Control": {
                  "Status": "通常",
                  "PublishingOffice": "熊本地方気象台",
                  "Title": "気象警報・注意報（Ｈ２７）"
                },
                "Head": {
                  "EventID": "weather-kumamoto-1",
                  "ReportDateTime": "2026-08-10T10:15:00+09:00",
                  "InfoType": "発表",
                  "Headline": { "Text": "熊本県では大雨に警戒してください。" }
                },
                "Body": {
                  "Warning": [{
                    "type_": "気象警報・注意報（市町村等）",
                    "Item": [{
                      "Kind": {
                        "Name": "大雨特別警報",
                        "Code": "33",
                        "Status": "発表"
                      },
                      "Areas": {
                        "Area": [{ "Name": "熊本市", "Code": "4310000" }]
                      }
                    }]
                  }]
                },
                "uuid_": "20260810011500_0_VPWW54_430000"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(
            json,
            AxisProviderOptions.DefaultChannel);
        Assert.AreEqual(AxisFrameKind.Data, frame.Kind);
        Assert.AreEqual(AxisProviderOptions.MeteorologyChannel, frame.Channel);
        Assert.AreEqual("VPWW54", frame.TelegramType);

        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.Xml!,
            SourceMode.Production,
            DateTimeOffset.Parse(
                "2026-08-10T01:15:01Z",
                CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent warning =
            Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual(WeatherWarningLevel.SpecialWarning, warning.MaximumLevel);
        Assert.AreEqual("熊本市", warning.Items.Single().AreaName);

        DisplayProgram program = new PageComposer().Compose(
            warning,
            AppSettings.CreateDefault().Display);
        Assert.AreEqual(OverlayPriority.WeatherSpecialWarning, program.Priority);
        Assert.IsTrue(program.Pages.SelectMany(page => page.Blocks).Any(block =>
            block.StyleToken == DisplayStyleTokens.WeatherSpecialWarning &&
            block.Badge == "大雨特別警報"));
    }

    [TestMethod]
    public void JmxMeteorologyDisasterPreventionBulletinUsesItsTelegramType()
    {
        const string json = """
            {
              "channel": "jmx-meteorology",
              "message": {
                "Control": {
                  "Status": "通常",
                  "PublishingOffice": "銚子地方気象台",
                  "Title": "府県気象防災速報"
                },
                "Head": {
                  "EventID": "weather-chiba-bulletin-1",
                  "ReportDateTime": "2026-08-10T12:01:00+09:00",
                  "InfoType": "発表",
                  "Title": "千葉県気象防災速報（線状降水帯発生）",
                  "Headline": { "Text": "千葉県で線状降水帯が発生しました。" }
                },
                "Body": {
                  "MeteorologicalInfos": {
                    "Item": {
                      "Areas": { "Area": { "Name": "千葉県", "Code": "120000" } }
                    }
                  }
                },
                "uuid_": "20260810030100_0_VPBS50_120000"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(
            json,
            AxisProviderOptions.DefaultChannel);
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.Xml!,
            SourceMode.Production,
            DateTimeOffset.Parse(
                "2026-08-10T03:01:01Z",
                CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent bulletin =
            Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual(
            WeatherInformationType.DisasterPreventionBulletin,
            bulletin.InformationType);
        Assert.AreEqual("VPBS50", bulletin.Issue.RawType);
        Assert.AreEqual("千葉県", bulletin.Items.Single().AreaName);
    }

    [TestMethod]
    public void UnsupportedMeteorologyTelegramIsSilentlyIgnored()
    {
        const string json = """
            {
              "channel": "jmx-meteorology",
              "message": {
                "Control": {
                  "Status": "通常",
                  "Title": "府県気象情報"
                },
                "Head": {
                  "EventID": "JPTF210010",
                  "ReportDateTime": "2026-08-10T23:10:00+09:00",
                  "InfoType": "発表"
                },
                "Body": {},
                "uuid_": "20260810141000_0_VPFJ50_140000"
              }
            }
            """;

        AxisDecodedFrame frame = AxisEnvelopeDecoder.Decode(
            json,
            AxisProviderOptions.DefaultChannel);
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            frame.Xml!,
            SourceMode.Production,
            DateTimeOffset.Parse(
                "2026-08-10T14:10:01Z",
                CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual(NormalizeStatus.Ignored, result.Status);
        Assert.AreEqual(0, result.Issues.Count);
    }

    [TestMethod]
    public void NestedUuidElementCanSupplyTelegramType()
    {
        const string xml = """
            <Report>
              <Metadata><uuid>20260810141000_0_VPOA50_130000</uuid></Metadata>
              <Control><Status>通常</Status><Title>未知の気象電文</Title></Control>
              <Head>
                <EventID>weather-tokyo-rain-1</EventID>
                <ReportDateTime>2026-08-10T23:10:00+09:00</ReportDateTime>
                <InfoType>発表</InfoType>
                <Headline><Text>東京都で記録的短時間大雨を観測しました。</Text></Headline>
              </Head>
              <Body>
                <MeteorologicalInfos>
                  <Item>
                    <Areas><Area><Name>東京都</Name><Code>130000</Code></Area></Areas>
                  </Item>
                </MeteorologicalInfos>
              </Body>
            </Report>
            """;
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());

        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            AxisProviderOptions.ProviderName,
            xml,
            SourceMode.Production,
            DateTimeOffset.Parse(
                "2026-08-10T14:10:01Z",
                CultureInfo.InvariantCulture))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        WeatherWarningEvent bulletin =
            Assert.IsInstanceOfType<WeatherWarningEvent>(result.Event);
        Assert.AreEqual(
            WeatherInformationType.RecordShortDurationHeavyRain,
            bulletin.InformationType);
        Assert.AreEqual("VPOA50", bulletin.Issue.RawType);
        Assert.AreEqual("東京都", bulletin.Items.Single().AreaName);
    }
}
