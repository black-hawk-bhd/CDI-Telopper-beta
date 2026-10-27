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
