using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class HistoryRehearsalLoaderTests
{
    [TestMethod]
    public async Task LoadsNormalizesCountsAndOrdersHistoryOldestFirst()
    {
        QuakeEvent fixture = DisplayEventFactory.CreateQuake(QuakeIssueType.DetailScale);
        QuakeEvent later = Copy(fixture, "later", fixture.IssuedAt.AddMinutes(2));
        QuakeEvent earlier = Copy(fixture, "earlier", fixture.IssuedAt.AddMinutes(-2));
        var source = new FakeHistoryMessageSource(["later", "ignored", "invalid", "earlier"]);
        var loader = new HistoryRehearsalLoader(
            source,
            new FakeNormalizer(new Dictionary<string, NormalizeResult>(StringComparer.Ordinal)
            {
                ["later"] = NormalizeResult.Success(later),
                ["ignored"] = NormalizeResult.Ignored(),
                ["invalid"] = NormalizeResult.Invalid(
                    new ValidationIssue("$", "invalid", ValidationSeverity.Error)),
                ["earlier"] = NormalizeResult.Success(earlier),
            }));
        AppSettings settings = AppSettings.CreateDefault();

        const string niiReportUrl = "https://agora.ex.nii.ac.jp/cgi-bin/cps/report_xml.pl?id=20260802045019_0_VXSE53_270000";
        const string localXmlFilePath = @"C:\test-data\VXSE53.xml";
        HistoryRehearsalLoadResult result = await loader.LoadAsync(
            new HistorySettings(HistoryApi.History, 500, 3)
            {
                NiiReportUrl = niiReportUrl,
                LocalXmlFilePath = localXmlFilePath,
            },
            settings.Provider);

        Assert.HasCount(2, result.Events);
        Assert.AreEqual("earlier", result.Events[0].Id.Value);
        Assert.AreEqual("later", result.Events[1].Id.Value);
        Assert.AreEqual(1, result.IgnoredCount);
        Assert.AreEqual(1, result.InvalidCount);
        Assert.IsNotNull(source.LastRequest);
        Assert.AreEqual(HistoryApi.History, source.LastRequest.Api);
        Assert.AreEqual(100, source.LastRequest.Limit);
        Assert.AreEqual(settings.Provider, source.LastRequest.Provider);
        Assert.AreEqual(niiReportUrl, source.LastRequest.NiiReportUrl);
        Assert.AreEqual(localXmlFilePath, source.LastRequest.LocalXmlFilePath);

        await loader.DisposeAsync();
        Assert.IsTrue(source.IsDisposed);
    }

    private static QuakeEvent Copy(QuakeEvent source, string id, DateTimeOffset issuedAt) => new(
        EventId.Create(id),
        source.Provider,
        issuedAt,
        issuedAt.AddSeconds(1),
        source.Signature,
        SourceMode.HistoryRehearsal,
        source.Issue with { IssuedAt = issuedAt },
        source.IssueType,
        source.Earthquake,
        source.Points,
        source.FreeFormComment);

    private sealed class FakeNormalizer(IReadOnlyDictionary<string, NormalizeResult> results)
        : IEventNormalizer
    {
        public NormalizeResult Normalize(RawProviderMessage raw) => results[raw.Json];
    }

    private sealed class FakeHistoryMessageSource(IReadOnlyList<string> payloads)
        : IHistoryMessageSource
    {
        public HistoryFetchRequest? LastRequest { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
            HistoryFetchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            IReadOnlyList<RawProviderMessage> messages = payloads
                .Select(payload => new RawProviderMessage(
                    "history-test",
                    payload,
                    SourceMode.HistoryRehearsal,
                    DateTimeOffset.UnixEpoch))
                .ToArray();
            return Task.FromResult(messages);
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
