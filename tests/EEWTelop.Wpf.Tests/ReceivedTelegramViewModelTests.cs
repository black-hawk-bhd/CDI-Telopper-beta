using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class ReceivedTelegramViewModelTests
{
    [TestMethod]
    public void EarthquakeReviewShowsReadingsForAreasAndSourceStations()
    {
        var issuedAt = new DateTimeOffset(2026, 11, 10, 12, 30, 0, TimeSpan.FromHours(9));
        var quake = new QuakeEvent(
            EventId.Create("quake-ruby"), "jma-xml", issuedAt, issuedAt, "revision-1", SourceMode.HistoryRehearsal,
            new IssueInfo("気象庁", issuedAt, "VXSE53", CorrectionType.None),
            QuakeIssueType.DetailScale,
            new EarthquakeInfo(issuedAt, null, null, JmaScale.FiveLowerOrMore, DomesticTsunami.None, ForeignTsunami.None),
            [
                new QuakePoint("北海道", "十勝地方中部", true, JmaScale.Three, "北海道十勝地方中部")
                {
                    SeismicAreaCode = "156",
                },
                new QuakePoint("北海道", "石狩市聚富＊", false, JmaScale.FiveLowerOrMore, "北海道石狩市")
                {
                    StationCode = "0123501",
                    MunicipalityName = "石狩市",
                },
                new QuakePoint("北海道", "石狩市花畔*", false, JmaScale.One, "北海道石狩市")
                {
                    StationCode = "0123522",
                    MunicipalityName = "石狩市",
                },
                new QuakePoint("熊本県", "球磨村渡", false, JmaScale.One, "熊本県球磨村"),
                new QuakePoint("北海道", "石狩市", false, JmaScale.One, "北海道石狩市")
                {
                    MunicipalityName = "石狩市",
                },
            ],
            string.Empty);
        var program = new PageComposer().Compose(quake, AppSettings.CreateDefault().Display);
        var review = new ReceivedTelegramViewModel(quake, program);

        var ruby = review.Pages.SelectMany(page => PlaceNameReadings.Split(page.Text, page.Places)).ToArray();
        Assert.IsTrue(ruby.Any(segment => segment is { Text: "十勝地方中部", Reading: "とかちちほうちゅうぶ" }));
        CollectionAssert.AreEqual(new[]
        {
            new ObservationStationReviewViewModel("石狩市聚富", "いしかりししっぷ", "震度5弱以上 未入電"),
            new ObservationStationReviewViewModel("石狩市花畔*", null, "震度1"),
            new ObservationStationReviewViewModel("球磨村渡", "くまむらわたり", "震度1"),
        }, review.ObservationStations.ToArray());
        Assert.AreEqual("石狩市聚富＊", quake.Points[1].Address);
        Assert.AreEqual("0123501", quake.Points[1].StationCode);
        Assert.AreEqual(SourceMode.HistoryRehearsal, review.Event.SourceMode);
        Assert.IsFalse(program.Pages.Any(page => page.AccessibleText.Contains("石狩市聚富", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TsunamiReviewUsesForecastAreaReadingWithOrWithoutCode()
    {
        var issuedAt = new DateTimeOffset(2026, 11, 10, 12, 30, 0, TimeSpan.FromHours(9));
        var tsunami = new TsunamiEvent(
            EventId.Create("tsunami-ruby"), "jma-xml", issuedAt, issuedAt, "revision-1", SourceMode.Production,
            new IssueInfo("気象庁", issuedAt, "VTSE41", CorrectionType.None),
            [
                new TsunamiArea(TsunamiGrade.Warning, false, "北海道太平洋沿岸東部", null, null)
                {
                    Code = "100",
                },
                new TsunamiArea(TsunamiGrade.Warning, false, "オホーツク海沿岸", null, null),
            ],
            false, null);
        var program = new PageComposer().Compose(tsunami, AppSettings.CreateDefault().Display);
        var review = new ReceivedTelegramViewModel(tsunami, program);

        var ruby = review.Pages.SelectMany(page => PlaceNameReadings.Split(page.Text, page.Places)).ToArray();
        Assert.IsTrue(ruby.Any(segment => segment is { Text: "北海道太平洋沿岸東部", Reading: "ほっかいどうたいへいようえんがんとうぶ" }));
        Assert.IsTrue(ruby.Any(segment => segment is { Text: "オホーツク海沿岸", Reading: "おほーつくかいえんがん" }));
        Assert.HasCount(0, review.ObservationStations);
    }

    [TestMethod]
    public void UnchangedDisplayResultIsVisibleInListAndDetails()
    {
        DateTimeOffset issuedAt = new(2026, 9, 3, 13, 35, 0, TimeSpan.FromHours(9));
        var disasterEvent = new WeatherWarningEvent(
            EventId.Create("weather-VPWW55-岡山地方気象台"),
            "axis",
            issuedAt,
            issuedAt.AddMinutes(3),
            "revision-2",
            SourceMode.Production,
            new IssueInfo("岡山地方気象台", issuedAt, "VPWW55", CorrectionType.None),
            "岡山県気象警報・注意報",
            [
                new WeatherWarningItem(
                    "倉敷市",
                    "3320200",
                    "レベル３大雨警報",
                    string.Empty,
                    WeatherWarningLevel.Warning,
                    "継続",
                    IsActive: true),
            ],
            isCancelled: false);
        var program = new DisplayProgram(
            "weather-program",
            disasterEvent.Id,
            disasterEvent.Kind,
            disasterEvent.SourceMode,
            disasterEvent.IssuedAt,
            OverlayPriority.WeatherWarning,
            [
                new DisplayPage(
                    1,
                    [
                        new DisplayBlock(
                            "継続",
                            "レベル３大雨警報。岡山県 倉敷市 継続中",
                            string.Empty,
                            DisplayStyleTokens.WeatherWarning),
                    ],
                    "レベル３大雨警報。岡山県 倉敷市 継続中",
                    null),
            ],
            issuedAt.ToUniversalTime(),
            EndPolicy.AutoHide,
            string.Empty);

        var viewModel = new ReceivedTelegramViewModel(
            disasterEvent,
            program,
            "表示対象の変更なし");

        Assert.Contains("[表示対象の変更なし]", viewModel.DisplayText);
        Assert.Contains("表示結果: 表示対象の変更なし", viewModel.DetailText);
        Assert.HasCount(1, viewModel.Pages);
    }
}
