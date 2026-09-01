using System.Net;
using System.Text;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Recovery;
using EEWTelop.Infrastructure.Axis.Transport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisRecoveryAndPolicyTests
{
    [TestMethod]
    public void WeatherTelegramPolicyAlwaysDiscardsLegacyWarningTelegrams()
    {
        Assert.IsFalse(AxisWeatherTelegramPolicy.ShouldAccept("VPWW53"));
        Assert.IsFalse(AxisWeatherTelegramPolicy.ShouldAccept("VPWW54"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW55"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW56"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW57"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW58"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW59"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW60"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPWW61"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.ShouldAccept("VPOA50"));
        Assert.AreEqual(
            "VPWW55",
            AxisWeatherTelegramPolicy.ReadTelegramType(
                "<Report uuid=\"20260810141000_0_VPWW55_140000\"><Control/><Head/></Report>"));
        Assert.AreEqual(
            "VPWW54",
            AxisWeatherTelegramPolicy.ReadTelegramType(
                "<Report uuid=\"20260812080411_0_VPWW54_130000\">" +
                "<Control/><Head/><Body><Warning><Type>hazard level</Type></Warning></Body>" +
                "</Report>"));
        Assert.IsFalse(AxisWeatherTelegramPolicy.ShouldAccept(
            AxisWeatherTelegramPolicy.ReadTelegramType(
                "<Report uuid=\"20260812080411_0_VPWW54_130000\">" +
                "<Control/><Head/><Body><Warning><Type>hazard level</Type></Warning></Body>" +
                "</Report>")));
    }

    [TestMethod]
    public void PerCategoryRouteMakesAllSupportedAxisTelegramsAvailable()
    {
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.MeteorologyChannel,
            "VPWW55"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VTSE41"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VTSE51"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VTSE52"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VXSE45"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.EewChannel,
            "AXIS-EEW"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VXSE43"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VXSE53"));
        Assert.IsFalse(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.SeismologyChannel,
            "VFVO50"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.VolcanologyChannel,
            "VFVO50"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedToAxis(
            AxisProviderOptions.VolcanologyChannel,
            "VFVO56"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VPWW55"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VTSE52"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VXSE45"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VXSE43"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VXSE53"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VYSE50"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VFVO50"));
        Assert.IsTrue(AxisWeatherTelegramPolicy.IsAssignedRecoveryTelegram("VFVO56"));
    }

    [TestMethod]
    public async Task OneShotRecoveryReadsRecentOfficialJmaAtomTelegram()
    {
        DateTimeOffset now = new(2026, 8, 12, 1, 0, 0, TimeSpan.Zero);
        const string telegram = """
            <Report>
              <Control><Title>気象警報・注意報</Title><DateTime>2026-08-12T00:59:00Z</DateTime><Status>通常</Status><Type>VPWW55</Type></Control>
              <Head><Title>気象警報・注意報</Title><ReportDateTime>2026-08-12T00:59:00Z</ReportDateTime><EventID>weather-test</EventID></Head>
              <Body />
            </Report>
            """;
        string feed = $$"""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <updated>{{now.AddMinutes(-1):O}}</updated>
                <link href="https://www.data.jma.go.jp/developer/xml/data/test.xml" />
              </entry>
            </feed>
            """;
        using var http = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("extra.xml", StringComparison.Ordinal)
                ? Xml(feed)
                : Xml(telegram)));
        var client = new AxisJmaAtomRecoveryClient(http, new FixedClock(now), new NullLog());
        var options = new AxisProviderOptions(
            new Uri("https://axis.prioris.jp/api/"),
            "token",
            AxisProviderOptions.MeteorologyChannel);
        var messages = new List<RawProviderMessage>();

        await foreach (RawProviderMessage message in client.FetchRecentAsync(
            now.AddMinutes(-5),
            options))
        {
            messages.Add(message);
        }

        Assert.HasCount(1, messages);
        Assert.AreEqual(RawProviderContentFormat.JmaXml, messages[0].ContentFormat);
        Assert.Contains("VPWW55", messages[0].Payload);
    }

    [TestMethod]
    public async Task ServerDiscoveryUsesBearerTokenAndNormalizesSocketPath()
    {
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"servers\":[\"wss://ws.axis.prioris.jp\"]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        var options = new AxisProviderOptions(
            new Uri("https://axis.prioris.jp/api/"),
            "secret-token",
            AxisProviderOptions.DefaultChannel);
        var client = new AxisApiClient(http, options);

        IReadOnlyList<Uri> servers = await client.GetServersAsync(CancellationToken.None);

        Assert.HasCount(1, servers);
        Assert.AreEqual("wss://ws.axis.prioris.jp/socket", servers[0].AbsoluteUri.TrimEnd('/'));
        Assert.AreEqual("Bearer", authorizationScheme);
        Assert.AreEqual("secret-token", authorizationParameter);
    }

    private static HttpResponseMessage Xml(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/xml"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }

    private sealed class NullLog : IAppLogWriter
    {
        public ValueTask WriteAsync(
            AppLogEntry entry,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
