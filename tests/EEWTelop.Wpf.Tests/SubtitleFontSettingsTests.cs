using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Infrastructure.Time;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SubtitleFontSettingsTests
{
    [TestMethod]
    public async Task ExistingSettingsUseTheDefaultAndTheSelectedFontRoundTrips()
    {
        string directory = Directory.CreateTempSubdirectory("cdi-font-").FullName;
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonSettingsStore(path, new UiLogBuffer());
            await store.SaveAsync(AppSettings.CreateDefault());
            JsonObject legacy = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.IsTrue(legacy["display"]!.AsObject().Remove("fontFamilyName"));
            await File.WriteAllTextAsync(path, legacy.ToJsonString());

            AppSettings loaded = await store.LoadAsync();
            Assert.AreEqual(DisplaySettings.DefaultFontFamilyName, loaded.Display.FontFamilyName);
            var editor = new SettingsEditorViewModel(loaded);
            Assert.AreEqual(DisplaySettings.DefaultFontFamilyName, editor.SubtitleFontOptions[0].Name);
            editor.FontFamilyName = " Yu Gothic UI ";
            await store.SaveAsync(editor.ToSettings(loaded));
            AppSettings saved = await store.LoadAsync();
            Assert.AreEqual("Yu Gothic UI", saved.Display.FontFamilyName);

            var reopened = new SettingsEditorViewModel(saved);
            reopened.ResetDisplaySettings();
            Assert.AreEqual(DisplaySettings.DefaultFontFamilyName, reopened.ToSettings(saved).Display.FontFamilyName);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public void FontChangeReachesTheCurrentSubtitleInPreviewAndObs()
    {
        DisplaySettings defaults = AppSettings.CreateDefault().Display;
        var clock = new SystemClock();
        DateTimeOffset now = clock.UtcNow;
        var store = new ObsSnapshotStore(defaults, now);
        var preview = new OverlayViewModel();
        var coordinator = new PriorityCoordinator(clock, defaults);
        var program = new DisplayProgram("font-test", EventId.Create("font-test"), EventKind.Quake,
            SourceMode.ManualTest, now, OverlayPriority.Quake,
            [new DisplayPage(1, [new DisplayBlock("", "字幕テスト", "", DisplayStyleTokens.Intensity)], "字幕テスト", null)],
            now, EndPolicy.AutoHide, "テスト");
        CoordinatorSnapshot snapshot = coordinator.Apply(program);
        store.Publish(snapshot, defaults, now);
        preview.Apply(snapshot, defaults);

        DisplaySettings updated = defaults with { FontFamilyName = "Meiryo" };
        preview.ApplySettings(updated);
        store.PublishSettings(updated, now);

        ObsViewSnapshot obs = store.Read(ObsViewChannel.General, now);
        Assert.AreEqual(program.ProgramId, obs.ProgramId);
        Assert.AreEqual(SourceMode.ManualTest, obs.SourceMode);
        Assert.AreEqual("字幕テスト", preview.Blocks.Single().PrimaryText);
        StringAssert.StartsWith(preview.SubtitleFontFamily.Source, "Meiryo, BIZ UDPGothic");
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(obs, ObsJsonContext.Default.ObsViewSnapshot));
        Assert.AreEqual("Meiryo", json.RootElement.GetProperty("fontFamilyName").GetString());

        store.ClearPrograms(now);
        Assert.AreEqual("Meiryo", store.Read(ObsViewChannel.General, now).FontFamilyName);
        preview.ApplySettings(defaults);
        StringAssert.StartsWith(preview.SubtitleFontFamily.Source, DisplaySettings.DefaultFontFamilyName);
    }
}
