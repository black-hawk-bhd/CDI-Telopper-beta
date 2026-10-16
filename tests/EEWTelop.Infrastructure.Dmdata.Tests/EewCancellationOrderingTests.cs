using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class EewCancellationOrderingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 3, 48, 10, TimeSpan.Zero);

    [TestMethod]
    [DataRow("dmdata.jp")]
    [DataRow("axis")]
    [DataRow("jma-xml")]
    public void XmlCreationTimeAllowsLaterCancellationWithLowerSerialAndOlderHeader(string provider)
    {
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        var cache = new EventVersionCache();
        EewEvent first = Assert.IsInstanceOfType<EewEvent>(normalizer.Normalize(Raw(provider, "42", "発表", 0)).Event);
        // Control/DateTime is new even though ReportDateTime was not updated.
        EewEvent cancel = Assert.IsInstanceOfType<EewEvent>(normalizer.Normalize(Raw(provider, "37", "取消", 40)).Event);
        Assert.AreEqual(first.IssuedAt, cancel.IssuedAt);
        Assert.AreEqual(Start.AddSeconds(40), cancel.SourceMessageTime);
        Assert.IsTrue(cache.TryAccept(first));
        Assert.IsTrue(cache.CheckAndAccept(cancel).Accepted);
        Assert.IsFalse(cache.TryAccept(first));
        Assert.AreEqual("37", cancel.Issue.Serial);
    }

    [TestMethod]
    public void MissingControlUsesExplicitHeaderButMalformedControlDoesNotUseReceiptTime()
    {
        var normalizer = new JmaXmlEventNormalizer(new EventSignatureBuilder());
        RawProviderMessage original = Raw("dmdata.jp", "37", "取消", 40);
        string controlTime = $"<DateTime>{Start.AddSeconds(40):O}</DateTime>";
        EewEvent missing = Assert.IsInstanceOfType<EewEvent>(normalizer.Normalize(original with
            { Json = original.Json.Replace(controlTime, "", StringComparison.Ordinal) }).Event);
        Assert.AreEqual(Start, missing.SourceMessageTime);
        EewEvent malformed = Assert.IsInstanceOfType<EewEvent>(normalizer.Normalize(original with
            { Json = original.Json.Replace(controlTime, "<DateTime>invalid</DateTime>", StringComparison.Ordinal) }).Event);
        Assert.IsNull(malformed.SourceMessageTime);
        var cache = new EventVersionCache();
        Assert.IsTrue(cache.TryAccept(Assert.IsInstanceOfType<EewEvent>(normalizer.Normalize(Raw("dmdata.jp", "42", "発表", 0)).Event)));
        Assert.IsTrue(cache.CheckAndAccept(malformed).RequiresReview);
    }

    [TestMethod]
    [DataRow("発表", "20261016123810")]
    [DataRow("取消", "")]
    public void UnconfirmedCancellationOrMissingEventIdIsHeldForReview(string infoType, string eventId)
    {
        RawProviderMessage raw = Raw("dmdata.jp", "37", infoType, 40);
        raw = raw with { Json = raw.Json.Replace("<Headline/>", "<Headline><Text>緊急地震速報を取消します</Text></Headline>", StringComparison.Ordinal)
            .Replace("<EventID>20261016123810</EventID>", $"<EventID>{eventId}</EventID>", StringComparison.Ordinal) };
        NormalizeResult result = new JmaXmlEventNormalizer(new EventSignatureBuilder()).Normalize(raw);
        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.Issues.Any(issue => issue.Path == "eew.ordering.reviewRequired"));
    }

    private static RawProviderMessage Raw(string provider, string serial, string infoType, int seconds) => new(provider,
        $"""
        <Report>
          <Control><Title>緊急地震速報（警報）</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice><DateTime>{Start.AddSeconds(seconds):O}</DateTime></Control>
          <Head><ReportDateTime>{Start:O}</ReportDateTime><EventID>20261016123810</EventID><Serial>{serial}</Serial><InfoType>{infoType}</InfoType><Headline/></Head>
          <Body/>
        </Report>
        """, SourceMode.Production, Start.AddMinutes(2)) { ContentFormat = RawProviderContentFormat.JmaXml };
}
