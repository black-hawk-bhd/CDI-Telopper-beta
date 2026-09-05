using System.Xml.Linq;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Axis.Normalization;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisEruptionDisplayTests
{
    [TestMethod]
    [DataRow("axis", SourceMode.Production)]
    [DataRow("axis", SourceMode.HistoryRehearsal)]
    [DataRow("dmdata.jp", SourceMode.Production)]
    public void ReceivedEruptionXmlShowsCompleteCommentWithoutThreeEarthquakePages(
        string provider,
        SourceMode mode)
    {
        XDocument document = LoadFixture();
        QuakeEvent quake = Normalize(document, provider, mode);
        DisplayProgram program = Compose(quake);
        DisplayBlock[] content = program.Pages.SelectMany(static page => page.Blocks)
            .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
            .ToArray();

        Assert.IsNull(quake.Earthquake.Hypocenter!.Magnitude);
        Assert.HasCount(0, quake.Points);
        Assert.IsTrue(content.All(static block => block.StyleToken == DisplayStyleTokens.Comment));
        StringAssert.Contains(program.Pages[0].AccessibleText, "クラカタウ火山で大規模な噴火が発生しました");
        Assert.AreEqual(
            WithoutWhitespace(quake.FreeFormComment),
            WithoutWhitespace(string.Concat(content.Select(static block => block.PrimaryText))));
        foreach (DisplayPage page in program.Pages)
        {
            Assert.DoesNotContain("インドネシア付近で地震", page.AccessibleText);
            Assert.DoesNotContain("Ｍ不明", page.AccessibleText);
            Assert.DoesNotContain("念のため津波に注意してください", page.AccessibleText);
            Assert.DoesNotContain("各地の詳しい震度情報はありません", page.AccessibleText);
        }
        Assert.AreEqual($"1 / {program.Pages.Count}", program.Pages[0].Blocks[^1].PrimaryText);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("今後の情報に注意してください。")]
    [DataRow("大規模な噴火が発生していません。")]
    [DataRow("大規模な噴火が発生した場合には情報を発表します。")]
    public void UnknownMagnitudeWithoutEruptionAnnouncementKeepsEarthquakePages(string comment)
    {
        XDocument document = LoadFixture();
        document.Descendants("FreeFormComment").Single().Value = comment;

        DisplayProgram program = Compose(Normalize(document));

        StringAssert.Contains(program.Pages[0].AccessibleText, "インドネシア付近で地震");
        StringAssert.Contains(program.Pages[1].AccessibleText, "念のため津波に注意してください");
        StringAssert.Contains(program.Pages[2].AccessibleText, "各地の詳しい震度情報はありません");
    }

    [TestMethod]
    public void ActualEarthquakeMagnitudeKeepsSummaryEvenWithEruptionComment()
    {
        XDocument document = LoadFixture();
        document.Descendants("Magnitude").Single().Value = "7.5";

        DisplayProgram program = Compose(Normalize(document));

        StringAssert.Contains(program.Pages[0].AccessibleText, "インドネシア付近で地震");
        StringAssert.Contains(program.Pages[0].AccessibleText, "7.5");
    }

    [TestMethod]
    public void ActualIntensityObservationKeepsEarthquakePages()
    {
        XDocument document = LoadFixture();
        document.Descendants("Body").Single().Add(XElement.Parse(
            "<Intensity><Observation><MaxInt>3</MaxInt></Observation></Intensity>"));

        DisplayProgram program = Compose(Normalize(document));

        StringAssert.Contains(program.Pages[0].AccessibleText, "インドネシア付近で地震");
    }

    [TestMethod]
    public void CorrectedEruptionKeepsCorrectionLabelAndComment()
    {
        XDocument document = LoadFixture();
        document.Descendants("InfoType").Single().Value = "訂正";

        DisplayProgram program = Compose(Normalize(document));

        Assert.AreEqual("訂正", program.Pages[0].Blocks[0].Badge);
        StringAssert.Contains(program.Pages[0].AccessibleText, "クラカタウ火山");
        Assert.IsFalse(program.Pages.Any(static page =>
            page.AccessibleText.Contains("インドネシア付近で地震", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CancelledEruptionStillShowsCancellation()
    {
        XDocument document = LoadFixture();
        document.Descendants("InfoType").Single().Value = "取消";

        DisplayProgram program = Compose(Normalize(document));

        Assert.HasCount(1, program.Pages);
        StringAssert.Contains(program.Pages[0].AccessibleText, "取り消");
        Assert.DoesNotContain("噴火が発生しました", program.Pages[0].AccessibleText);
    }

    private static XDocument LoadFixture() => XDocument.Load(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "vxse53-large-eruption.xml"));

    private static QuakeEvent Normalize(
        XDocument document,
        string provider = "axis",
        SourceMode mode = SourceMode.Production)
    {
        var signatureBuilder = new EventSignatureBuilder();
        var normalizer = new AxisEventNormalizer(
            new JmaXmlEventNormalizer(signatureBuilder), signatureBuilder);
        NormalizeResult result = normalizer.Normalize(new RawProviderMessage(
            provider, document.ToString(), mode,
            new DateTimeOffset(2026, 9, 4, 21, 32, 8, TimeSpan.Zero))
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
        });
        Assert.IsTrue(result.IsSuccess);
        return Assert.IsInstanceOfType<QuakeEvent>(result.Event);
    }

    private static DisplayProgram Compose(QuakeEvent quake) =>
        new PageComposer().Compose(quake, AppSettings.CreateDefault().Display);

    private static string WithoutWhitespace(string value) =>
        string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));
}
