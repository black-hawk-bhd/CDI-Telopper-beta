using EEWTelop.Application.Events;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class Phase2FormattingAndSignatureTests
{
    [TestMethod]
    public void ScaleFormatterCoversDocumentedValues()
    {
        var expected = new Dictionary<double, string>
        {
            [10] = "1",
            [20] = "2",
            [30] = "3",
            [40] = "4",
            [45] = "5弱",
            [46] = "5弱以上",
            [50] = "5強",
            [55] = "6弱",
            [60] = "6強",
            [70] = "7",
        };

        foreach ((double raw, string display) in expected)
        {
            Assert.AreEqual(display, ScaleFormatter.Format(ScaleFormatter.Normalize(raw)));
        }

        Assert.AreEqual(JmaScale.FiveLower, ScaleFormatter.Normalize(4.5));
        Assert.AreEqual("?", ScaleFormatter.Format(ScaleFormatter.Normalize(123)));
    }

    [TestMethod]
    public void MagnitudeFormatterAlwaysUsesOneDecimalPlace()
    {
        Assert.AreEqual("6.0", MagnitudeFormatter.Format(6));
        Assert.AreEqual("5.0", MagnitudeFormatter.Format(5.0));
        Assert.AreEqual("7.4", MagnitudeFormatter.Format(7.4));
        Assert.AreEqual("-", MagnitudeFormatter.Format(null));
        Assert.AreEqual("-", MagnitudeFormatter.Format(-1));
        Assert.AreEqual("-", MagnitudeFormatter.Format(double.NaN));
    }

    [TestMethod]
    public void PlaceNormalizerKeepsPrefectureInMunicipalityKey()
    {
        string tokyo = PlaceNormalizer.BuildDisplayName("東京都", "府中市押立町", isArea: false);
        string hiroshima = PlaceNormalizer.BuildDisplayName("広島県", "府中市府川町", isArea: false);

        Assert.AreEqual("東京都府中市", tokyo);
        Assert.AreEqual("広島県府中市", hiroshima);
        Assert.AreNotEqual(tokyo, hiroshima);
    }

    [TestMethod]
    public void SignatureIsOrderIndependentButDetectsMaterialCorrections()
    {
        var builder = new EventSignatureBuilder();
        QuakeEvent original = CreateQuake(
            "固定震源",
            depth: 10,
            magnitude: 6.0,
            [
                new QuakePoint("東京都", "府中市", false, JmaScale.FiveLower, "東京都府中市"),
                new QuakePoint("広島県", "府中市", false, JmaScale.Four, "広島県府中市"),
            ]);
        QuakeEvent reordered = CreateQuake(
            "固定震源",
            depth: 10,
            magnitude: 6.0,
            original.Points.Reverse().ToArray());
        QuakeEvent replacedPoint = CreateQuake(
            "固定震源",
            depth: 10,
            magnitude: 6.0,
            [
                original.Points[0],
                new QuakePoint("広島県", "別の市", false, JmaScale.Four, "広島県別の市"),
            ]);

        Assert.AreEqual(builder.Build(original), builder.Build(reordered));
        Assert.AreNotEqual(builder.Build(original), builder.Build(replacedPoint));
        Assert.AreNotEqual(
            builder.Build(original),
            builder.Build(CreateQuake("訂正震源", 10, 6.0, original.Points)));
        Assert.AreNotEqual(
            builder.Build(original),
            builder.Build(CreateQuake("固定震源", 20, 6.0, original.Points)));
        Assert.AreNotEqual(
            builder.Build(original),
            builder.Build(CreateQuake("固定震源", 10, 6.1, original.Points)));
    }

    private static QuakeEvent CreateQuake(
        string hypocenterName,
        int depth,
        double magnitude,
        IReadOnlyList<QuakePoint> points)
    {
        DateTimeOffset issuedAt = new(2026, 7, 31, 3, 1, 0, TimeSpan.Zero);
        var issue = new IssueInfo(
            "気象庁",
            issuedAt,
            "DetailScale",
            CorrectionType.None);
        var earthquake = new EarthquakeInfo(
            new DateTimeOffset(2026, 7, 31, 3, 0, 0, TimeSpan.Zero),
            ArrivalTime: null,
            new HypocenterInfo(
                hypocenterName,
                string.Empty,
                35.0,
                139.0,
                depth,
                magnitude,
                string.Empty),
            JmaScale.FiveLower,
            DomesticTsunami.None,
            ForeignTsunami.Unknown);

        return new QuakeEvent(
            EventId.Create("signature-fixture"),
            "P2PQuake",
            issuedAt,
            issuedAt.AddSeconds(1),
            string.Empty,
            SourceMode.Production,
            issue,
            QuakeIssueType.DetailScale,
            earthquake,
            points,
            string.Empty);
    }
}
