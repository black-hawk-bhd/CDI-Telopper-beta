using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class EewHokkaidoLabelTests
{
    private static readonly string[] HokkaidoOnly = ["北海道"];
    private static readonly string[] HokkaidoAndKanto = ["北海道", "関東"];
    private static readonly string[] InterleavedLabels = ["石川", "北海道", "新潟"];
    private static readonly string[] WideAreaLabels =
        ["北海道", "東北", "新潟", "関東", "北陸", "甲信", "東海", "伊豆諸島"];

    [TestMethod]
    [DataRow("北海道道央", "北海道道南")]
    [DataRow("北海道利尻礼文", "北海道奥尻島")]
    [DataRow("北海道", "北海道利尻礼文")]
    [DataRow("北海道利尻礼文", "奥尻島")]
    [DataRow("利尻・礼文", "北海道奥尻島")]
    public void MultipleHokkaidoLabelsCollapseEvenInSmallAnnouncements(string first, string second)
    {
        CollectionAssert.AreEqual(HokkaidoOnly, EewAreaLabelFormatter.Format([Area(first), Area(second)]).ToArray());
    }

    [TestMethod]
    [DataRow("北海道道央")]
    [DataRow("北海道道南")]
    [DataRow("北海道道北")]
    [DataRow("北海道道東")]
    [DataRow("北海道利尻礼文")]
    [DataRow("北海道奥尻島")]
    [DataRow("奥尻島")]
    public void SingleHokkaidoLabelKeepsExistingDetail(string name)
    {
        var labels = EewAreaLabelFormatter.Format([Area(name), Area(name)]);
        Assert.HasCount(1, labels);
        Assert.AreEqual(name, labels[0]);
    }

    [TestMethod]
    public void MultipleSourceAreasInOneForecastDistrictDoNotLoseExistingSingleDisplayLabel()
    {
        EewArea[] areas = [Area(string.Empty, "石狩地方北部"), Area(string.Empty, "石狩地方中部")];
        var labels = EewAreaLabelFormatter.Format(areas);
        Assert.HasCount(1, labels);
        Assert.AreEqual("北海道道央", labels[0]);
    }

    [TestMethod]
    public void HokkaidoKeepsItsFirstPositionWithoutMovingOtherRegions()
    {
        EewArea[] areas = [Area("石川県"), Area("北海道道北"), Area("新潟県"), Area("北海道奥尻島")];
        CollectionAssert.AreEqual(InterleavedLabels, EewAreaLabelFormatter.Format(areas).ToArray());
    }

    [TestMethod]
    public void HokkaidoReductionDoesNotChangeOtherRegionalAggregationThresholds()
    {
        EewArea[] areas = [Area("北海道道央"), Area("北海道道南"), Area("北海道道北"), Area("北海道道東"),
            Area("埼玉県"), Area("千葉県"), Area("東京都"), Area("神奈川県")];
        CollectionAssert.AreEqual(HokkaidoAndKanto, EewAreaLabelFormatter.Format(areas).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WideAreaWarningCombinesHokkaidoAndIslandsInBothLayoutsWithoutChangingSource(bool vertical)
    {
        EewArea[] areas = [Area("北海道道央"), Area("北海道道南"), Area("北海道道北"), Area("北海道道東"),
            Area("北海道利尻礼文"), Area("青森県"), Area("岩手県"), Area("北海道奥尻島"),
            Area("新潟県"), Area("東京都"), Area("神奈川県"), Area("石川県"), Area("富山県"),
            Area("山梨県"), Area("長野県"), Area("静岡県"), Area("愛知県"), Area("伊豆大島"), Area("三宅島")];
        string[] originalNames = areas.Select(a => a.Name).ToArray();
        var eew = DisplayEventFactory.CreateEew(areas, hypocenterName: "千島列島");
        var program = new PageComposer().Compose(eew, DisplayEventFactory.Settings with { UseVerticalEewWarningLayout = vertical });
        var page = program.Pages[0];
        Assert.AreEqual(string.Join('　', WideAreaLabels), page.Blocks.Single(b => b.StyleToken == DisplayStyleTokens.EewAreas).PrimaryText);
        CollectionAssert.AreEqual(WideAreaLabels, page.Blocks[0].EewCard!.Areas.ToArray());
        CollectionAssert.AreEqual(originalNames, eew.Areas.Select(a => a.Name).ToArray());
        Assert.AreEqual(SourceMode.Production, program.SourceMode);
        Assert.AreEqual("千島列島", eew.Earthquake!.Hypocenter!.Name);
    }

    private static EewArea Area(string prefecture, string? name = null) =>
        new(prefecture, name ?? prefecture, JmaScale.FiveLower, 50, EewWarningKind.ForecastNotArrived, null);
}
