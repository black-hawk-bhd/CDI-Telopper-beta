using EEWTelop.Wpf.ViewModels;
using EEWTelop.Application.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class SubtitlePhraseTemplateViewModelTests
{
    [TestMethod]
    public void ChangedAndEmptyPhrasesAreSavedButDefaultsAreOmitted()
    {
        var viewModel = new SubtitlePhraseTemplateViewModel(null);
        viewModel.Phrases.Single(phrase => phrase.Id == "quake.tsunami.caution").Text =
            "任意の注意文";
        viewModel.Phrases.Single(phrase => phrase.Id == "quake.tsunami.checking").Text =
            string.Empty;

        IReadOnlyDictionary<string, string> overrides = viewModel.BuildOverrides();

        Assert.AreEqual("任意の注意文", overrides["quake.tsunami.caution"]);
        Assert.AreEqual(string.Empty, overrides["quake.tsunami.checking"]);
        Assert.IsFalse(overrides.ContainsKey("quake.tsunami.none"));
    }

    [TestMethod]
    public void ResetAllRestoresCatalogDefaultsAndClearsOverrides()
    {
        var viewModel = new SubtitlePhraseTemplateViewModel(
            new Dictionary<string, string>
            {
                ["quake.tsunami.caution"] = "変更済み",
            });

        viewModel.ResetAll();

        Assert.IsEmpty(viewModel.BuildOverrides());
    }

    [TestMethod]
    public void SettingsEditorCarriesPhraseOverridesIntoSavedSettings()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var settingsEditor = new SettingsEditorViewModel(defaults);
        settingsEditor.SetSubtitlePhraseOverrides(new Dictionary<string, string>
        {
            ["quake.tsunami.caution"] = "任意の注意文",
        });

        AppSettings saved = settingsEditor.ToSettings(defaults);

        Assert.AreEqual(
            "任意の注意文",
            saved.Display.SubtitlePhraseOverrides["quake.tsunami.caution"]);
    }
}
