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
    public void TsunamiDowngradeGradeBadgeDefaultsOnAndCanBeTurnedOffAndReset()
    {
        var settings = AppSettings.CreateDefault();
        var oldDisplay = JsonNode.Parse(JsonSerializer.Serialize(settings.Display))!.AsObject();
        oldDisplay.Remove(nameof(DisplaySettings.ShowTsunamiDowngradeGradeBadge));
        Assert.IsTrue(JsonSerializer.Deserialize<DisplaySettings>(oldDisplay.ToJsonString())!.ShowTsunamiDowngradeGradeBadge);

        var editor = new SettingsEditorViewModel(settings) { ShowTsunamiDowngradeGradeBadge = false };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(settings)))!;
        var loaded = new SettingsEditorViewModel(saved);
        Assert.IsFalse(loaded.ShowTsunamiDowngradeGradeBadge);
        loaded.ResetDisplaySettings();
        Assert.IsTrue(loaded.ToSettings(saved).Display.ShowTsunamiDowngradeGradeBadge);
    }

    [TestMethod]
    public void OmitTsunamiCautionDefaultsOffAndPersistsThroughTheEditor()
    {
        var settings = AppSettings.CreateDefault();
        var oldDisplay = JsonNode.Parse(JsonSerializer.Serialize(settings.Display))!.AsObject();
        oldDisplay.Remove(nameof(DisplaySettings.OmitScalePromptCautionWhenTsunamiWarningActive));
        oldDisplay.Remove(nameof(DisplaySettings.SuppressScalePromptWhenTsunamiWarningActive));
        Assert.IsFalse(JsonSerializer.Deserialize<DisplaySettings>(oldDisplay.ToJsonString())!
            .OmitScalePromptCautionWhenTsunamiWarningActive);
        Assert.IsFalse(JsonSerializer.Deserialize<DisplaySettings>(oldDisplay.ToJsonString())!
            .SuppressScalePromptWhenTsunamiWarningActive);

        var editor = new SettingsEditorViewModel(settings) { OmitScalePromptCautionWhenTsunamiWarningActive = true };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(settings)))!;
        var loaded = new SettingsEditorViewModel(saved);
        Assert.IsTrue(loaded.OmitScalePromptCautionWhenTsunamiWarningActive);
        loaded.ResetDisplaySettings();
        Assert.IsFalse(loaded.ToSettings(saved).Display.OmitScalePromptCautionWhenTsunamiWarningActive);
    }

    [TestMethod]
    public void WholeScalePromptSuppressionCanBeSavedAndResetIndependently()
    {
        var settings = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(settings) { SuppressScalePromptWhenTsunamiWarningActive = true };
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(editor.ToSettings(settings)))!;
        var loaded = new SettingsEditorViewModel(saved);
        Assert.IsTrue(loaded.SuppressScalePromptWhenTsunamiWarningActive);
        Assert.IsFalse(loaded.OmitScalePromptCautionWhenTsunamiWarningActive);
        loaded.ResetDisplaySettings();
        Assert.IsFalse(loaded.ToSettings(saved).Display.SuppressScalePromptWhenTsunamiWarningActive);
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
