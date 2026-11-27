using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EewVerticalPresentationTests
{
    private static readonly string[] HokurikuLabels = ["石川", "新潟", "富山", "長野"];
    private static readonly string[] IshikawaLabel = ["石川"];
    private static readonly string[] ToyamaLabel = ["富山"];
    private static readonly string[] LiteralLabel = ["検証 地域｜A<B>"];

    [TestMethod]
    public void WarningCarriesStructuredRegionsWithoutChangingHorizontalTextOrSource()
    {
        EewArea[] areas = [Area("石川県"), Area("新潟県"), Area("富山県"), Area("長野県")];
        var eew = DisplayEventFactory.CreateEew(areas, hypocenterName: "能登半島沖");
        var composer = new PageComposer();
        var horizontal = composer.Compose(eew, DisplayEventFactory.Settings);
        var vertical = composer.Compose(eew, DisplayEventFactory.Settings with { UseVerticalEewWarningLayout = true });
        var card = vertical.Pages[0].Blocks[0].EewCard;
        Assert.IsNotNull(card);
        Assert.AreEqual("緊急地震速報（気象庁）", card.Title);
        Assert.AreEqual("能登半島沖で地震", card.Message);
        Assert.AreEqual("強い揺れに警戒", card.Instruction);
        CollectionAssert.AreEqual(HokurikuLabels, card.Areas.ToArray());
        Assert.IsFalse(card.IsCancelled);
        Assert.AreEqual(horizontal.Pages[0].AccessibleText, vertical.Pages[0].AccessibleText);
        CollectionAssert.AreEqual(horizontal.Pages[0].Blocks.Select(b => b.PrimaryText).ToArray(),
            vertical.Pages[0].Blocks.Select(b => b.PrimaryText).ToArray());
        Assert.AreEqual(eew.SourceMode, vertical.SourceMode);
        Assert.AreEqual("能登半島沖", eew.Earthquake!.Hypocenter!.Name);
    }

    [TestMethod]
    public void ForecastDoesNotOptIntoVerticalPresentation()
    {
        var forecast = DisplayEventFactory.CreateEew(isWarning: false);
        var program = new PageComposer().Compose(forecast,
            DisplayEventFactory.Settings with { UseVerticalEewWarningLayout = true });
        Assert.IsTrue(program.Pages[0].Blocks.All(b => b.EewCard is null));
        StringAssert.Contains(program.Pages[0].AccessibleText, "今後の情報に注意");
    }

    [TestMethod]
    public void CancellationHasNoStaleRegionsAndRetainsCustomizedExplanation()
    {
        var settings = DisplayEventFactory.Settings with
        {
            UseVerticalEewWarningLayout = true,
            SubtitlePhraseOverrides = new() { ["eew.cancel"] = "先ほどの緊急地震速報は取り消されました" },
        };
        var program = new PageComposer().Compose(DisplayEventFactory.CreateEew(cancelled: true), settings);
        var card = program.Pages[0].Blocks[0].EewCard;
        Assert.IsNotNull(card);
        Assert.IsTrue(card.IsCancelled);
        Assert.AreEqual("緊急地震速報（取消）", card.Title);
        Assert.IsEmpty(card.Areas);
        Assert.AreEqual("先ほどの緊急地震速報は取り消されました", card.Message);
        StringAssert.Contains(program.Pages[0].AccessibleText, card.Message);
    }

    [TestMethod]
    public void ConcurrentWarningsKeepSeparateCardsAndCancellationUpdatesOnlyItsSlot()
    {
        var settings = DisplayEventFactory.Settings with { UseVerticalEewWarningLayout = true };
        var composer = new PageComposer();
        var concurrent = new ConcurrentEewProgramComposer();
        var first = DisplayEventFactory.CreateEew([Area("石川県")], eventId: "one", hypocenterName: "能登半島沖");
        var second = DisplayEventFactory.CreateEew([Area("富山県")], eventId: "two", hypocenterName: "富山湾");
        concurrent.Compose(first, composer.Compose(first, settings), settings);
        var program = concurrent.Compose(second, composer.Compose(second, settings), settings);
        var cards = program.Pages[0].Blocks.Where(b => b.EewCard is not null).Select(b => b.EewCard!).ToArray();
        Assert.HasCount(2, cards);
        Assert.AreEqual("能登半島沖で地震", cards[0].Message);
        CollectionAssert.AreEqual(IshikawaLabel, cards[0].Areas.ToArray());
        Assert.AreEqual("富山湾で地震", cards[1].Message);
        CollectionAssert.AreEqual(ToyamaLabel, cards[1].Areas.ToArray());
        var cancel = DisplayEventFactory.CreateEew(cancelled: true, eventId: "one", issuedAt: first.IssuedAt.AddSeconds(3));
        program = concurrent.Compose(cancel, composer.Compose(cancel, settings), settings);
        cards = program.Pages[0].Blocks.Where(b => b.EewCard is not null).Select(b => b.EewCard!).ToArray();
        Assert.HasCount(2, cards);
        Assert.HasCount(1, cards.Where(c => c.IsCancelled).ToArray());
        Assert.AreEqual("富山湾で地震", cards.Single(c => !c.IsCancelled).Message);
    }

    [TestMethod]
    public void ContinuedWarningHighlightsOnlyNewWarningDistrictsEvenAfterAutoHide()
    {
        var settings = DisplayEventFactory.Settings with { HighlightEewAreaChanges = true };
        var composer = new PageComposer();
        var concurrent = new ConcurrentEewProgramComposer();
        EewArea ishikawa = Area("石川県");
        EewArea toyama = Area("富山県");
        EewEvent first = DisplayEventFactory.CreateEew([ishikawa, toyama]);
        var initial = concurrent.Compose(first, composer.Compose(first, settings), settings);
        Assert.IsTrue(initial.Pages[0].Blocks.All(block => block.EewAreaLabels is null));

        EewEvent next = DisplayEventFactory.CreateEew(
            [ishikawa, toyama with { ScaleFrom = JmaScale.FiveUpper }, Area("新潟県")],
            issuedAt: first.IssuedAt.AddMinutes(2));
        var plain = composer.Compose(next, settings);
        var updated = concurrent.Compose(next, plain, settings);
        DisplayBlock areas = updated.Pages[0].Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas);
        Assert.AreEqual("石川　富山　新潟", areas.PrimaryText);
        Assert.AreEqual(plain.Pages[0].AccessibleText, updated.Pages[0].AccessibleText);
        Assert.IsNotNull(areas.EewAreaLabels);
        Assert.AreEqual("新潟", areas.EewAreaLabels.Single(label => label.IsChanged).Name);
        Assert.Contains(new EewAreaLabel("富山", false), areas.EewAreaLabels);
        Assert.AreSame(areas.EewAreaLabels, updated.Pages[0].Blocks[0].EewAreaLabels);

        EewEvent identical = DisplayEventFactory.CreateEew(next.Areas.Reverse().ToArray(), issuedAt: next.IssuedAt.AddSeconds(1));
        var unchanged = concurrent.Compose(identical, composer.Compose(identical, settings), settings);
        Assert.IsTrue(unchanged.Pages[0].Blocks.All(block => block.EewAreaLabels is null));
    }

    [TestMethod]
    public void NewWarningDistrictsRespectTheSettingAndHighlightTheirGroupedLabel()
    {
        var disabled = DisplayEventFactory.Settings;
        var enabled = disabled with { HighlightEewAreaChanges = true };
        var composer = new PageComposer();
        var concurrent = new ConcurrentEewProgramComposer();
        EewArea[] areas = [Area("東京都"), Area("神奈川県"), Area("埼玉県"), Area("千葉県"),
            Area("石川県"), Area("富山県"), Area("福井県"), Area("新潟県")];
        EewEvent first = DisplayEventFactory.CreateEew(areas);
        concurrent.Compose(first, composer.Compose(first, disabled), disabled);

        areas = [.. areas, Area("山梨県")];
        EewEvent next = DisplayEventFactory.CreateEew(areas, issuedAt: first.IssuedAt.AddSeconds(1));
        var off = concurrent.Compose(next, composer.Compose(next, disabled), disabled);
        Assert.IsTrue(off.Pages[0].Blocks.All(block => block.EewAreaLabels is null));

        var training = DisplayEventFactory.CreateEew([Area("石川県")], sourceMode: SourceMode.Production, isTest: true);
        var test = concurrent.Compose(training, composer.Compose(training, enabled), enabled);
        Assert.IsTrue(test.Pages[0].Blocks.All(block => block.EewAreaLabels is null));

        areas = [.. areas, Area("茨城県"), areas[4] with { Name = "石川県加賀", ScaleFrom = JmaScale.SixLower }];
        EewEvent continued = DisplayEventFactory.CreateEew(areas, issuedAt: next.IssuedAt.AddSeconds(1));
        var on = concurrent.Compose(continued, composer.Compose(continued, enabled), enabled);
        var labels = on.Pages[0].Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas).EewAreaLabels;
        Assert.IsNotNull(labels);
        Assert.AreEqual("関東", labels.Single(label => label.IsChanged).Name);
        Assert.Contains(new EewAreaLabel("北陸", false), labels);

        EewEvent narrowed = DisplayEventFactory.CreateEew(
            areas.Skip(1).Reverse().ToArray(), issuedAt: continued.IssuedAt.AddSeconds(1));
        var noAdditions = concurrent.Compose(narrowed, composer.Compose(narrowed, enabled), enabled);
        Assert.IsTrue(noAdditions.Pages[0].Blocks.All(block => block.EewAreaLabels is null));
    }

    [TestMethod]
    public void WarningWithNoPreviousDistrictsKeepsAllLabelsWhite()
    {
        var settings = DisplayEventFactory.Settings with { HighlightEewAreaChanges = true };
        var composer = new PageComposer();
        var concurrent = new ConcurrentEewProgramComposer();
        EewEvent first = DisplayEventFactory.CreateEew([]);
        concurrent.Compose(first, composer.Compose(first, settings), settings);

        EewEvent next = DisplayEventFactory.CreateEew([Area("石川県")], issuedAt: first.IssuedAt.AddSeconds(1));
        var program = concurrent.Compose(next, composer.Compose(next, settings), settings);
        Assert.IsTrue(program.Pages[0].Blocks.All(block => block.EewAreaLabels is null));
    }

    [TestMethod]
    public void TrainingRetainsBannerAndRegionNamesAreNotSplitOrInterpretedAsMarkup()
    {
        var eew = DisplayEventFactory.CreateEew([Area("検証 地域｜A<B>")], isTest: true, sourceMode: SourceMode.ManualTest);
        var program = new PageComposer().Compose(eew, DisplayEventFactory.Settings with { UseVerticalEewWarningLayout = true });
        Assert.AreEqual(SourceMode.ManualTest, program.SourceMode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(program.RehearsalLabel));
        CollectionAssert.AreEqual(LiteralLabel, program.Pages[0].Blocks[0].EewCard!.Areas.ToArray());
    }

    private static EewArea Area(string prefecture) =>
        new(prefecture, prefecture, JmaScale.FiveLower, 50, EewWarningKind.ForecastNotArrived, null);
}
