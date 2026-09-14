using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class TelegramReviewCategoryTests
{
    [TestMethod]
    public void MixedWeatherLevelsIncludeReleasesWithoutMatchingOtherKinds()
    {
        var now = DateTimeOffset.UtcNow;
        var weather = new WeatherWarningEvent(EventId.Create("mixed"), "jma-xml", now, now, "mixed",
            SourceMode.Production, new IssueInfo("JMA", now, "VPWW55", CorrectionType.None), "",
            [new("A市", "0710000", "大雨警報", "03", WeatherWarningLevel.Warning, "解除", false),
             new("B市", "0720000", "大雨注意報", "10", WeatherWarningLevel.Advisory, "発表", true)], false);
        Assert.IsTrue(TelegramReviewCategoryFilter.Matches(weather, TelegramReviewCategory.Warning));
        Assert.IsTrue(TelegramReviewCategoryFilter.Matches(weather, TelegramReviewCategory.Advisory));
        Assert.IsTrue(TelegramReviewCategoryFilter.Matches(weather, TelegramReviewCategory.Weather));
        Assert.IsFalse(TelegramReviewCategoryFilter.Matches(weather, TelegramReviewCategory.SpecialWarning));
        Assert.IsFalse(TelegramReviewCategoryFilter.Matches(weather, TelegramReviewCategory.Quake));
    }

    [TestMethod]
    public void ObservationTelegramIsSeparateEvenWithForecastAreasOrNoObservations()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (string code in new[] { "VTSE41", "VTSE51", "VTSE52" })
        {
            var tsunami = new TsunamiEvent(EventId.Create(code), "axis", now, now, code,
                SourceMode.HistoryRehearsal, new IssueInfo("JMA", now, code, CorrectionType.None),
                [new(TsunamiGrade.Warning, false, "A沿岸", null, null)], false, null);
            Assert.AreEqual(code != "VTSE41", TelegramReviewCategoryFilter.Matches(tsunami, TelegramReviewCategory.TsunamiObservation));
            Assert.AreEqual(code == "VTSE41", TelegramReviewCategoryFilter.Matches(tsunami, TelegramReviewCategory.TsunamiForecast));
        }
    }

    [TestMethod]
    public void QuakeAndEewRemainSeparateAndAllPreservesEveryScenario()
    {
        var scenarios = TestScenarioCatalog.Create(DateTimeOffset.UtcNow);
        foreach (var scenario in scenarios)
        {
            Assert.IsTrue(TelegramReviewCategoryFilter.Matches(scenario.Event, TelegramReviewCategory.All));
            Assert.AreEqual(scenario.Event is QuakeEvent, TelegramReviewCategoryFilter.Matches(scenario.Event, TelegramReviewCategory.Quake));
            Assert.AreEqual(scenario.Event is EewEvent, TelegramReviewCategoryFilter.Matches(scenario.Event, TelegramReviewCategory.Eew));
            Assert.AreEqual(scenario.Event is VolcanoEvent, TelegramReviewCategoryFilter.Matches(scenario.Event, TelegramReviewCategory.Volcano));
        }
    }
}
