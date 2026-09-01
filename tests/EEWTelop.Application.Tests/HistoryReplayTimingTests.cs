using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class HistoryReplayTimingTests
{
    [TestMethod]
    public void MultiPageItemWaitsForEveryPageEvenWhenHistoryIntervalIsShorter()
    {
        DisplaySettings display = AppSettings.CreateDefault().Display with
        {
            PageDurationSeconds = 4,
        };
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.ScalePrompt,
            [DisplayEventFactory.Point(1, JmaScale.Four)],
            DomesticTsunami.Checking);
        DisplayProgram program = new PageComposer().Compose(quake, display);

        TimeSpan duration = HistoryReplayTiming.GetItemDuration(
            program,
            display,
            intervalSeconds: 3);

        Assert.AreEqual(3, program.Pages.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(12), duration);
    }

    [TestMethod]
    public void LongerHistoryIntervalRemainsTheMinimumForSinglePageItems()
    {
        DisplaySettings display = AppSettings.CreateDefault().Display;
        QuakeEvent quake = DisplayEventFactory.CreateQuake(
            QuakeIssueType.Other,
            comment: "固定コメント");
        DisplayProgram program = new PageComposer().Compose(quake, display);

        TimeSpan duration = HistoryReplayTiming.GetItemDuration(
            program,
            display,
            intervalSeconds: 9);

        Assert.AreEqual(1, program.Pages.Count);
        Assert.AreEqual(TimeSpan.FromSeconds(9), duration);
    }
}
