using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class ReceivedTelegramViewModelTests
{
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
