using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Infrastructure.Dmdata.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class DmdataWebSocketFrameDecoderTests
{
    [TestMethod]
    public void GzipBase64RawFrameReturnsOriginalJmaXml()
    {
        const string xml = "<Report><Control><Title>震度速報</Title></Control></Report>";
        string frame = JsonSerializer.Serialize(new
        {
            type = "data",
            head = new { type = "VXSE51", test = false },
            format = "xml",
            compression = "gzip",
            encoding = "base64",
            body = Convert.ToBase64String(Gzip(xml)),
        });

        DmdataDecodedFrame decoded = DmdataWebSocketFrameDecoder.Decode(frame);

        Assert.AreEqual(DmdataFrameKind.Data, decoded.Kind);
        Assert.AreEqual("VXSE51", decoded.TelegramType);
        Assert.AreEqual(xml, decoded.Xml);
        Assert.IsFalse(decoded.IsTest);
    }

    [TestMethod]
    public void Vxse45DmdataFrameNormalizesOnlyWarningAreas()
    {
        const string xml = """
            <Report>
              <Control><Title>緊急地震速報（地震動予報）</Title><Status>通常</Status><Type>VXSE45</Type><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head><Title>緊急地震速報（地震動予報）</Title><ReportDateTime>2024-04-17T23:14:59+09:00</ReportDateTime><EventID>20240417231454</EventID><InfoType>発表</InfoType><Serial>4</Serial><Headline><Text>強い揺れ</Text><Information><Item><Kind><Name>緊急地震速報（警報）</Name><Code>31</Code></Kind></Item></Information></Headline></Head>
              <Body><Intensity><Forecast><Pref><Name>愛媛</Name><Area><Name>愛媛県南予</Name><Category><Kind><Name>緊急地震速報（警報）</Name><Code>10</Code></Kind></Category><ForecastInt><From>5-</From><To>5-</To></ForecastInt></Area><Area><Name>愛媛県東予</Name><Category><Kind><Name>緊急地震速報（予報）</Name><Code>01</Code></Kind></Category><ForecastInt><From>3</From><To>4</To></ForecastInt></Area></Pref></Forecast></Intensity></Body>
            </Report>
            """;
        string frame = JsonSerializer.Serialize(new
        {
            type = "data",
            head = new { type = "VXSE45", test = false },
            format = "xml",
            compression = "gzip",
            encoding = "base64",
            body = Convert.ToBase64String(Gzip(xml)),
        });

        DmdataDecodedFrame decoded = DmdataWebSocketFrameDecoder.Decode(frame);
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            "dmdata.jp",
            decoded.Xml!,
            SourceMode.Production,
            new DateTimeOffset(2024, 4, 17, 14, 14, 59, TimeSpan.Zero))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });

        Assert.AreEqual("VXSE45", decoded.TelegramType);
        Assert.AreEqual(NormalizeStatus.Success, result.Status);
        EewEvent eew = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.IsTrue(eew.IsWarning);
        Assert.HasCount(1, eew.Areas);
        Assert.AreEqual("愛媛県南予", eew.Areas[0].Name);
        Assert.AreEqual(EewWarningKind.ForecastNotArrived, eew.Areas[0].WarningKind);
    }

    [TestMethod]
    public void PingFrameCreatesMatchingPong()
    {
        DmdataDecodedFrame ping = DmdataWebSocketFrameDecoder.Decode(
            "{\"type\":\"ping\",\"pingId\":\"abc123\"}");
        using JsonDocument pong = JsonDocument.Parse(
            DmdataWebSocketFrameDecoder.CreatePong(ping.PingId));

        Assert.AreEqual(DmdataFrameKind.Ping, ping.Kind);
        Assert.AreEqual("pong", pong.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("abc123", pong.RootElement.GetProperty("pingId").GetString());
    }

    [TestMethod]
    public void ZipBase64RawFrameReturnsOriginalJmaXml()
    {
        const string xml = "<Report><Control><Title>震度速報</Title></Control></Report>";
        string frame = JsonSerializer.Serialize(new
        {
            type = "data",
            head = new { type = "VXSE51", test = true },
            format = "xml",
            compression = "zip",
            encoding = "base64",
            body = Convert.ToBase64String(Zip(xml)),
        });

        DmdataDecodedFrame decoded = DmdataWebSocketFrameDecoder.Decode(frame);

        Assert.AreEqual(DmdataFrameKind.Data, decoded.Kind);
        Assert.AreEqual("VXSE51", decoded.TelegramType);
        Assert.AreEqual(xml, decoded.Xml);
        Assert.IsTrue(decoded.IsTest);
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }

    private static byte[] Zip(string text)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("telegram.xml", CompressionLevel.SmallestSize);
            using Stream stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }
}
