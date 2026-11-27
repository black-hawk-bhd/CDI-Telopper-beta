using EEWTelop.Wpf.ViewModels;
using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Settings;
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
    public void TsunamiDowngradeNoteSuffixCanBeEditedSavedAndReset()
    {
        const string id = "tsunami.warning.downgrade.suffix";
        var defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults);
        var dialog = new SubtitlePhraseTemplateViewModel(editor.SubtitlePhraseOverrides);
        var phrase = dialog.Phrases.Single(item => item.Id == id);
        Assert.AreEqual("から切り替えられました。", phrase.Text);

        phrase.Text = "から変更になりました。";
        editor.SetSubtitlePhraseOverrides(dialog.BuildOverrides());
        AppSettings saved = JsonSettingsStore.NormalizeDocument(editor.ToSettings(defaults));
        var restored = new SubtitlePhraseTemplateViewModel(saved.Display.SubtitlePhraseOverrides);
        Assert.AreEqual("から変更になりました。", restored.Phrases.Single(item => item.Id == id).Text);

        restored.ResetAll();
        Assert.AreEqual("から切り替えられました。", restored.Phrases.Single(item => item.Id == id).Text);
        Assert.IsFalse(restored.BuildOverrides().ContainsKey(id));
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
