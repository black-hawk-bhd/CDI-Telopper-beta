using System.Net;
using System.Text;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class NiiJmaXmlHistoryMessageSourceTests
{
    [TestMethod]
    public async Task FetchesOnlySelectedDisasterCodesAndUsesXmlCache()
    {
        string cache = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-NiiHistoryTests-{Guid.NewGuid():N}");
        try
        {
            var handler = new NiiHandler();
            using var httpClient = new HttpClient(handler);
            var source = new NiiJmaXmlHistoryMessageSource(
                httpClient,
                cache,
                new FakeClock(),
                requestInterval: TimeSpan.Zero);
            HistoryFetchRequest request = CreateRequest(NiiHistoryContent.QuakeAndTsunami);

            IReadOnlyList<RawProviderMessage> first = await source.FetchAsync(request);
            IReadOnlyList<RawProviderMessage> second = await source.FetchAsync(request);

            Assert.HasCount(3, first);
            Assert.HasCount(3, second);
            Assert.IsTrue(first.All(static item => item.Provider == NiiJmaXmlHistoryMessageSource.ProviderName));
            Assert.IsTrue(first.All(static item => item.ContentFormat == RawProviderContentFormat.JmaXml));
            Assert.IsTrue(first.All(static item => item.SourceMode == SourceMode.HistoryRehearsal));
            Assert.AreEqual(
                new DateTimeOffset(2026, 8, 2, 4, 50, 19, TimeSpan.Zero),
                first.Single(static item => item.Payload.Contains("震源・震度に関する情報", StringComparison.Ordinal)).ReceivedAt);
            Assert.AreEqual(
                new DateTimeOffset(2026, 8, 2, 3, 10, 11, TimeSpan.Zero),
                first.Single(static item => item.Payload.Contains("津波警報・注意報・予報", StringComparison.Ordinal)).ReceivedAt);
            Assert.AreEqual(2, handler.IndexRequestCount);
            Assert.AreEqual(3, handler.TelegramRequestCount);
            Assert.HasCount(3, Directory.GetFiles(cache, "*.xml"));

            await source.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task QuakeOnlyExcludesTsunamiAndEewCodes()
    {
        string cache = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-NiiHistoryTests-{Guid.NewGuid():N}");
        try
        {
            var handler = new NiiHandler();
            using var httpClient = new HttpClient(handler);
            var source = new NiiJmaXmlHistoryMessageSource(
                httpClient,
                cache,
                new FakeClock(),
                requestInterval: TimeSpan.Zero);

            IReadOnlyList<RawProviderMessage> messages = await source.FetchAsync(
                CreateRequest(NiiHistoryContent.QuakeOnly));

            Assert.HasCount(2, messages);
            Assert.IsTrue(messages.Any(static message =>
                message.Payload.Contains("震源・震度に関する情報", StringComparison.Ordinal)));
            Assert.IsTrue(messages.Any(static message =>
                message.Payload.Contains("南海トラフ地震臨時情報", StringComparison.Ordinal)));
            Assert.AreEqual(2, handler.TelegramRequestCount);
            await source.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task WeatherWarningsOnlyFetchesVpww55ThroughVpww61()
    {
        string cache = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-NiiWeatherHistoryTests-{Guid.NewGuid():N}");
        try
        {
            var handler = new NiiHandler();
            using var httpClient = new HttpClient(handler);
            var source = new NiiJmaXmlHistoryMessageSource(
                httpClient,
                cache,
                new FakeClock(),
                requestInterval: TimeSpan.Zero);

            IReadOnlyList<RawProviderMessage> messages = await source.FetchAsync(
                CreateRequest(NiiHistoryContent.WeatherWarningsOnly));

            Assert.HasCount(7, messages);
            Assert.AreEqual(7, handler.TelegramRequestCount);
            Assert.IsTrue(messages.All(static message =>
                message.Payload.Contains("気象警報・注意報", StringComparison.Ordinal)));
            await source.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(NiiHistoryContent.WeatherRain, "VPWW55")]
    [DataRow(NiiHistoryContent.WeatherLandslide, "VPWW56")]
    [DataRow(NiiHistoryContent.WeatherStormSurge, "VPWW57")]
    [DataRow(NiiHistoryContent.WeatherStorm, "VPWW58")]
    [DataRow(NiiHistoryContent.WeatherWave, "VPWW59")]
    [DataRow(NiiHistoryContent.WeatherHeavySnow, "VPWW60")]
    [DataRow(NiiHistoryContent.WeatherOtherAdvisories, "VPWW61")]
    public async Task IndividualWeatherSelectionFetchesOnlyRequestedTelegram(
        NiiHistoryContent content,
        string expectedCode)
    {
        string cache = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-NiiWeatherCodeTests-{Guid.NewGuid():N}");
        try
        {
            var handler = new NiiHandler();
            using var httpClient = new HttpClient(handler);
            var source = new NiiJmaXmlHistoryMessageSource(
                httpClient,
                cache,
                new FakeClock(),
                requestInterval: TimeSpan.Zero);

            IReadOnlyList<RawProviderMessage> messages = await source.FetchAsync(
                CreateRequest(content));

            Assert.HasCount(1, messages);
            Assert.HasCount(1, handler.RequestedReportIds);
            StringAssert.Contains(handler.RequestedReportIds[0], $"_{expectedCode}_");
            await source.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task HighVolumeDailyIndexAboveTwoMibIsAcceptedWithinSafetyLimit()
    {
        string cache = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-NiiLargeIndexTests-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(new LargeDailyIndexHandler());
            var source = new NiiJmaXmlHistoryMessageSource(
                httpClient,
                cache,
                new FakeClock(),
                requestInterval: TimeSpan.Zero);

            IReadOnlyList<RawProviderMessage> messages = await source.FetchAsync(
                CreateRequest(NiiHistoryContent.WeatherRain));

            Assert.HasCount(1, messages);
            StringAssert.Contains(messages[0].Payload, "レベル２大雨注意報");
            await source.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ExtractXmlDecodesEscapedJmaTelegram()
    {
        const string html = "<html><pre>&lt;Report&gt;&lt;Head&gt;&lt;EventID&gt;1&lt;/EventID&gt;&lt;/Head&gt;&lt;/Report&gt;</pre></html>";

        string xml = NiiJmaXmlHistoryMessageSource.ExtractXml(html);

        Assert.AreEqual("<Report><Head><EventID>1</EventID></Head></Report>", xml);
    }

    [TestMethod]
    [DataRow("https://agora.ex.nii.ac.jp/cgi-bin/cps/report_each.pl?id=20260802045019_0_VXSE53_270000")]
    [DataRow("https://agora.ex.nii.ac.jp/cgi-bin/cps/report_xml.pl?id=20260802045019_0_VXSE53_270000")]
    public async Task DirectReportUrlFetchesExactlyOneTelegramWithoutDailyIndex(string reportUrl)
    {
        string cache = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-NiiDirectTests-{Guid.NewGuid():N}");
        try
        {
            var handler = new NiiHandler();
            using var httpClient = new HttpClient(handler);
            var source = new NiiJmaXmlHistoryMessageSource(
                httpClient,
                cache,
                new FakeClock(),
                requestInterval: TimeSpan.Zero);
            HistoryFetchRequest request = CreateRequest(NiiHistoryContent.TsunamiOnly) with
            {
                NiiDate = new DateOnly(2000, 1, 1),
                NiiReportUrl = reportUrl,
            };

            IReadOnlyList<RawProviderMessage> first = await source.FetchAsync(request);
            IReadOnlyList<RawProviderMessage> second = await source.FetchAsync(request);

            Assert.HasCount(1, first);
            Assert.HasCount(1, second);
            StringAssert.Contains(first[0].Payload, "震源・震度に関する情報");
            Assert.AreEqual(0, handler.IndexRequestCount);
            Assert.AreEqual(1, handler.TelegramRequestCount);
            Assert.AreEqual(
                "20260802045019_0_VXSE53_270000",
                handler.RequestedReportIds.Single());
            await source.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("http://agora.ex.nii.ac.jp/cgi-bin/cps/report_xml.pl?id=20260802045019_0_VXSE53_270000")]
    [DataRow("https://example.com/cgi-bin/cps/report_xml.pl?id=20260802045019_0_VXSE53_270000")]
    [DataRow("https://agora.ex.nii.ac.jp/cgi-bin/cps/report_day.pl?id=20260802045019_0_VXSE53_270000")]
    [DataRow("https://agora.ex.nii.ac.jp/cgi-bin/cps/report_xml.pl?id=invalid")]
    [DataRow("https://agora.ex.nii.ac.jp/cgi-bin/cps/report_xml.pl?id=20260802045019_0_VXSE53_270000&x=1")]
    public void DirectReportUrlRejectsUntrustedOrMalformedAddresses(string reportUrl)
    {
        Assert.ThrowsExactly<InvalidDataException>(
            () => NiiJmaXmlHistoryMessageSource.ExtractReportIdFromUrl(reportUrl));
    }

    [TestMethod]
    public void ArchiveTimestampPreservesSameMinuteTelegramOrder()
    {
        DateTimeOffset? scalePrompt = NiiJmaXmlHistoryMessageSource.ReadArchiveReceivedAt(
            "20251202144713_0_VXSE51_010000");
        DateTimeOffset? destination = NiiJmaXmlHistoryMessageSource.ReadArchiveReceivedAt(
            "20251202144752_0_VXSE52_010000");

        Assert.IsNotNull(scalePrompt);
        Assert.IsNotNull(destination);
        DateTimeOffset scalePromptValue = scalePrompt.GetValueOrDefault();
        DateTimeOffset destinationValue = destination.GetValueOrDefault();
        Assert.IsTrue(scalePromptValue < destinationValue);
        Assert.AreEqual(TimeSpan.Zero, scalePromptValue.Offset);
    }

    private static HistoryFetchRequest CreateRequest(NiiHistoryContent content) => new(
        HistoryApi.NiiJmaXml,
        Limit: 10,
        AppSettings.CreateDefault().Provider)
    {
        NiiDate = DateOnly.FromDateTime(DateTime.Today),
        NiiContent = content,
    };

    private sealed class NiiHandler : HttpMessageHandler
    {
        public int IndexRequestCount { get; private set; }

        public int TelegramRequestCount { get; private set; }

        public List<string> RequestedReportIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsNotNull(request.RequestUri);
            Assert.AreEqual("https", request.RequestUri.Scheme);
            Assert.AreEqual("agora.ex.nii.ac.jp", request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("report_day.pl", StringComparison.Ordinal))
            {
                IndexRequestCount++;
                const string index = """
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802045019_0_VXSE53_270000">quake</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802045019_0_VXSE53_270000">duplicate</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802040000_0_VYSE50_010000">nankai-trough</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802031011_0_VTSE41_270000">tsunami</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802020000_0_VXSE43_010000">eew</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802010000_0_VPWW54_010000">weather</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015955_0_VPWW55_010000">rain</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015956_0_VPWW56_010000">landslide</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015957_0_VPWW57_010000">storm-surge</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015958_0_VPWW58_010000">storm</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015959_0_VPWW59_010000">wave</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015960_0_VPWW60_010000">snow</a>
                    <a href="/cgi-bin/cps/report_each.pl?id=20260802015961_0_VPWW61_010000">other</a>
                    """;
                return Task.FromResult(Html(index));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("report_xml.pl", StringComparison.Ordinal))
            {
                TelegramRequestCount++;
                string id = ParseQueryId(request.RequestUri.Query);
                RequestedReportIds.Add(id);
                string xml = id.Contains("VYSE50", StringComparison.Ordinal)
                    ? NankaiTroughXml
                    : id.Contains("VXSE53", StringComparison.Ordinal)
                        ? QuakeXml
                        : id.Contains("VPWW", StringComparison.Ordinal)
                            ? WeatherXml
                            : TsunamiXml;
                string html = $"<html><body><pre>{WebUtility.HtmlEncode(xml)}</pre></body></html>";
                return Task.FromResult(Html(html));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Html(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/html"),
        };

        private static string ParseQueryId(string query)
        {
            const string prefix = "?id=";
            Assert.IsTrue(query.StartsWith(prefix, StringComparison.Ordinal));
            return Uri.UnescapeDataString(query[prefix.Length..]);
        }

        private const string QuakeXml = """
            <Report xmlns:jmx_eb="http://xml.kishou.go.jp/jmaxml1/elementBasis1/">
              <Control><Title>震源・震度に関する情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
              <Head><ReportDateTime>2026-08-02T13:50:00+09:00</ReportDateTime><EventID>20260802134745</EventID><Serial>1</Serial><InfoType>発表</InfoType></Head>
              <Body><Earthquake><OriginTime>2026-08-02T13:47:00+09:00</OriginTime><Hypocenter><Area><Name>熊本県熊本地方</Name><jmx_eb:Coordinate>+32.5+130.6-10000/</jmx_eb:Coordinate></Area></Hypocenter><jmx_eb:Magnitude>3.2</jmx_eb:Magnitude></Earthquake><Intensity><Observation><MaxInt>3</MaxInt></Observation></Intensity></Body>
            </Report>
            """;

        private const string NankaiTroughXml = """
            <Report><Control><Title>南海トラフ地震臨時情報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control><Head><Title>南海トラフ地震臨時情報（調査中）</Title><ReportDateTime>2026-08-02T13:00:00+09:00</ReportDateTime><TargetDateTime>2026-08-02T13:00:00+09:00</TargetDateTime><EventID>20260802130000</EventID><InfoType>発表</InfoType><Headline><Text>南海トラフ地震との関連性について調査を開始しました。</Text></Headline></Head><Body><EarthquakeInfo type="南海トラフ地震に関連する情報"><InfoSerial><Name>調査中</Name><Code>111</Code></InfoSerial></EarthquakeInfo></Body></Report>
            """;

        private const string TsunamiXml = """
            <Report><Control><Title>津波警報・注意報・予報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control><Head><ReportDateTime>2026-08-02T12:00:00+09:00</ReportDateTime><EventID>20260802120000</EventID><Serial>1</Serial><InfoType>発表</InfoType></Head><Body><Tsunami><Forecast><Item><Category><Name>津波注意報</Name></Category><Area><Name>北海道太平洋沿岸東部</Name></Area></Item></Forecast></Tsunami></Body></Report>
            """;

        private const string WeatherXml = """
            <Report><Control><Title>気象警報・注意報（Ｒ０６）（大雨）</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control><Head><ReportDateTime>2026-08-02T11:59:00+09:00</ReportDateTime><EventID>20260802115900</EventID><InfoType>発表</InfoType></Head><Body><MeteorologicalInfos type="気象警報・注意報"><MeteorologicalInfo><Item><Kind><Name>レベル３大雨警報</Name><Code>L3</Code><Status>発表</Status></Kind><Areas><Area><Name>札幌市</Name><Code>0110000</Code></Area></Areas></Item></MeteorologicalInfo></MeteorologicalInfos></Body></Report>
            """;
    }

    private sealed class LargeDailyIndexHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsNotNull(request.RequestUri);
            if (request.RequestUri.AbsolutePath.EndsWith("report_day.pl", StringComparison.Ordinal))
            {
                string index = new string(' ', 3 * 1024 * 1024) +
                    "<a href=\"/cgi-bin/cps/report_each.pl?id=20260802015955_0_VPWW55_010000\">rain</a>";
                return Task.FromResult(Html(index));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("report_xml.pl", StringComparison.Ordinal))
            {
                const string xml = """
                    <Report>
                      <Control><Title>気象警報・注意報（Ｒ０６）（大雨）</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
                      <Head><ReportDateTime>2026-08-02T11:59:00+09:00</ReportDateTime><InfoType>発表</InfoType></Head>
                      <Body><Warning type="気象警報・注意報（市町村等）"><Item><Kind><Name>レベル２大雨注意報</Name><Code>10</Code><Status>発表</Status></Kind><Area><Name>札幌市</Name><Code>0110000</Code></Area></Item></Warning></Body>
                    </Report>
                    """;
                return Task.FromResult(Html(
                    $"<html><body><pre>{WebUtility.HtmlEncode(xml)}</pre></body></html>"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Html(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/html"),
        };
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 2, 6, 0, 0, TimeSpan.Zero);

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
