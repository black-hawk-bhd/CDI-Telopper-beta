using System.Text.Json;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SimulatorRehearsalBannerTests
{
    [TestMethod]
    [DataRow(false, SourceMode.ManualTest, "操作テスト／訓練")]
    [DataRow(true, SourceMode.ManualTest, "")]
    [DataRow(true, SourceMode.Sandbox, "操作テスト／訓練")]
    public void PreviewAndObsHideOnlyPresentation(bool hide, SourceMode mode, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = AppSettings.CreateDefault().Display;
        var page = new DisplayPage(0, [new DisplayBlock("", "訓練本文は保持", "", DisplayStyleTokens.Summary)], "本文", null);
        var program = new DisplayProgram("sim-test", EventId.Create("simulator:test"), EventKind.Quake,
            mode, now, OverlayPriority.Quake, [page], now, EndPolicy.AutoHide, "操作テスト／訓練")
            { HideSimulatorTrainingBanner = hide };
        var snapshot = new CoordinatorSnapshot(program, page, 0, TimeSpan.Zero, now, null, null, [], null,
            new CoordinatorDecision(default, "test"), false);
        var overlay = new OverlayViewModel();
        overlay.Apply(snapshot, settings);
        Assert.AreEqual(expected, overlay.RehearsalLabel);
        var store = new ObsSnapshotStore(settings, now);
        var obs = store.Publish(snapshot, settings, now);
        Assert.AreEqual(expected, obs.RehearsalLabel);
        Assert.AreEqual(mode, obs.SourceMode);
        Assert.AreEqual("訓練本文は保持", obs.Blocks[0].PrimaryText);
        Assert.AreEqual("操作テスト／訓練", program.RehearsalLabel);
    }

    [TestMethod]
    public void HiddenBannerPermissionCannotSurviveSerialization()
    {
        var now = DateTimeOffset.UtcNow;
        var program = new DisplayProgram("sim", EventId.Create("simulator:test"), EventKind.Quake,
            SourceMode.ManualTest, now, OverlayPriority.Quake, [], now, EndPolicy.AutoHide, "訓練")
            { HideSimulatorTrainingBanner = true };
        string json = JsonSerializer.Serialize(program);
        Assert.IsFalse(json.Contains("HideSimulatorTrainingBanner", StringComparison.Ordinal));
        var restored = JsonSerializer.Deserialize<DisplayProgram>(json)!;
        Assert.IsFalse(restored.HideSimulatorTrainingBanner);
        Assert.AreEqual(SourceMode.ManualTest, restored.SourceMode);
        Assert.AreEqual("訓練", restored.RehearsalLabel);
    }
}
