using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Configuration;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class IntensityPageSettingsTests
{
    [TestMethod]
    public void MissingSettingDefaultsOnAndExplicitOffRoundTrips()
    {
        var settings = AppSettings.CreateDefault().Display;
        var json = JsonNode.Parse(JsonSerializer.Serialize(settings))!.AsObject();
        json.Remove(nameof(DisplaySettings.SeparateIntensityPagesByScale));
        Assert.IsTrue(JsonSerializer.Deserialize<DisplaySettings>(json.ToJsonString())!.SeparateIntensityPagesByScale);
        settings = settings with { SeparateIntensityPagesByScale = false };
        Assert.IsFalse(JsonSerializer.Deserialize<DisplaySettings>(JsonSerializer.Serialize(settings))!.SeparateIntensityPagesByScale);
    }

    [TestMethod]
    public void EditorLoadsSavesAndResetsIntensityPageSetting()
    {
        var settings = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(settings);
        Assert.IsTrue(editor.SeparateIntensityPagesByScale);
        editor.SeparateIntensityPagesByScale = false;
        var saved = editor.ToSettings(settings);
        Assert.IsFalse(saved.Display.SeparateIntensityPagesByScale);
        var loaded = new SettingsEditorViewModel(saved);
        Assert.IsFalse(loaded.SeparateIntensityPagesByScale);
        loaded.ResetDisplaySettings();
        Assert.IsTrue(loaded.ToSettings(saved).Display.SeparateIntensityPagesByScale);
    }

    [TestMethod]
    public void WeatherAreaRowsSettingDefaultsToTwoAndPersistsThreeRowChoice()
    {
        var settings = AppSettings.CreateDefault();
        var json = JsonNode.Parse(JsonSerializer.Serialize(settings.Display))!.AsObject();
        json.Remove(nameof(DisplaySettings.LimitActiveWeatherAreaRowsToTwo));
        Assert.IsTrue(JsonSerializer.Deserialize<DisplaySettings>(json.ToJsonString())!.LimitActiveWeatherAreaRowsToTwo);

        var editor = new SettingsEditorViewModel(settings) { LimitActiveWeatherAreaRowsToTwo = false };
        var saved = editor.ToSettings(settings);
        Assert.IsFalse(saved.Display.LimitActiveWeatherAreaRowsToTwo);
        var loaded = new SettingsEditorViewModel(saved);
        Assert.IsFalse(loaded.LimitActiveWeatherAreaRowsToTwo);
        loaded.ResetDisplaySettings();
        Assert.IsTrue(loaded.ToSettings(saved).Display.LimitActiveWeatherAreaRowsToTwo);
    }
}
