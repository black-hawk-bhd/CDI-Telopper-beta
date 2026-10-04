using EEWTelop.Application.Configuration;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class QuakeFilterSettingsTests
{
    [TestMethod]
    public void DefaultEditorHasEightIndependentIntensityChoicesAndNationwideRegions()
    {
        var editor = new SettingsEditorViewModel(AppSettings.CreateDefault());
        Assert.HasCount(8, editor.QuakeIntensityFilters);
        Assert.IsTrue(editor.QuakeIntensityFilters.All(option => !option.IsExcluded));
        Assert.AreEqual("震度1", editor.QuakeIntensityFilters[0].Label);
        Assert.AreEqual("震度6強", editor.QuakeIntensityFilters[^1].Label);
        Assert.AreEqual("全国", editor.QuakePrefectureSelectionSummary);
        Assert.IsFalse(editor.ShowLowIntensityPointsInStrongQuakes);
    }

    [TestMethod]
    public void EditorRoundTripAndFilterResetKeepNewSelectionsIndependentOfWeather()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var editor = new SettingsEditorViewModel(defaults);
        editor.QuakeIntensityFilters.Single(option => option.Scale == JmaScale.Four).IsExcluded = true;
        editor.QuakeIntensityFilters.Single(option => option.Scale == JmaScale.SixUpper).IsExcluded = true;
        editor.SetQuakePrefectureCodes(["13", "04", "13", "invalid"]);
        editor.SetWeatherPrefectureCodes(["01"]);
        editor.QuakePrefectureMode = QuakePrefectureFilterMode.PointsOnly;
        editor.ShowLowIntensityPointsInStrongQuakes = true;
        AppSettings saved = editor.ToSettings(defaults);
        Assert.HasCount(2, saved.Filter.ExcludedQuakeMaximumScales!);
        Assert.IsFalse(saved.Filter.HideQuakeBelowIntensity3);
        Assert.HasCount(2, saved.Filter.QuakePrefectureCodes);
        Assert.AreEqual("01", saved.Filter.WeatherPrefectureCodes.Single());
        Assert.AreEqual(QuakePrefectureFilterMode.PointsOnly, saved.Filter.QuakePrefectureMode);
        Assert.IsTrue(saved.Display.ShowLowIntensityPointsInStrongQuakes);
        var restored = new SettingsEditorViewModel(saved);
        Assert.AreEqual(2, restored.QuakeIntensityFilters.Count(option => option.IsExcluded));
        restored.ResetFilterSettings();
        Assert.IsTrue(restored.QuakeIntensityFilters.All(option => !option.IsExcluded));
        Assert.HasCount(0, restored.QuakePrefectureCodes);
        Assert.AreEqual(QuakePrefectureFilterMode.EventAndPoints, restored.QuakePrefectureMode);
        Assert.IsFalse(restored.ShowLowIntensityPointsInStrongQuakes);
    }

    [TestMethod]
    public void SchemaTwentySixMigrationKeepsContractAndLegacyIntensityChoice()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings old = defaults with
        {
            SchemaVersion = 26,
            Provider = defaults.Provider with { DmdataEewContractType = DmdataEewContractType.Warning },
            Filter = defaults.Filter with { HideQuakeBelowIntensity3 = true },
        };
        AppSettings migrated = JsonSettingsStore.NormalizeDocument(old);
        Assert.AreEqual(AppSettings.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.AreEqual(DmdataEewContractType.Warning, migrated.Provider.DmdataEewContractType);
        Assert.HasCount(2, migrated.Filter.ExcludedQuakeMaximumScales!);
        Assert.Contains(JmaScale.One, migrated.Filter.ExcludedQuakeMaximumScales!);
        Assert.Contains(JmaScale.Two, migrated.Filter.ExcludedQuakeMaximumScales!);
        var editor = new SettingsEditorViewModel(migrated);
        Assert.IsTrue(editor.HideQuakeBelowIntensity3);
        editor.QuakeIntensityFilters.Single(option => option.Scale == JmaScale.One).IsExcluded = false;
        Assert.IsFalse(editor.HideQuakeBelowIntensity3);
        Assert.HasCount(1, editor.ToSettings(migrated).Filter.ExcludedQuakeMaximumScales!);
    }

    [TestMethod]
    public void SettingsNormalizationRejectsUnsupportedExclusionsAndNormalizesRegionsAndMode()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        AppSettings normalized = JsonSettingsStore.NormalizeDocument(defaults with
        {
            Filter = defaults.Filter with
            {
                HideQuakeBelowIntensity3 = true,
                ExcludedQuakeMaximumScales = [JmaScale.SixUpper, JmaScale.Seven, JmaScale.Unknown,
                    JmaScale.FiveLowerOrMore, JmaScale.SixUpper],
                QuakePrefectureCodes = [" 13 ", "13", "invalid", "04"],
                QuakePrefectureMode = (QuakePrefectureFilterMode)999,
            },
        });
        Assert.AreEqual(JmaScale.SixUpper, normalized.Filter.ExcludedQuakeMaximumScales!.Single());
        Assert.IsFalse(normalized.Filter.HideQuakeBelowIntensity3);
        Assert.HasCount(2, normalized.Filter.QuakePrefectureCodes);
        Assert.AreEqual("04", normalized.Filter.QuakePrefectureCodes[0]);
        Assert.AreEqual(QuakePrefectureFilterMode.EventAndPoints, normalized.Filter.QuakePrefectureMode);
    }

    [TestMethod]
    [DataRow(QuakePrefectureFilterMode.EventAndPoints)]
    [DataRow(QuakePrefectureFilterMode.EventOnly)]
    [DataRow(QuakePrefectureFilterMode.PointsOnly)]
    public async Task NewSettingsSurviveJsonSaveAndReload(QuakePrefectureFilterMode mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "cdi-quake-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AppSettings defaults = AppSettings.CreateDefault();
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"), new UiLogBuffer());
            await store.SaveAsync(defaults with
            {
                Filter = defaults.Filter with
                {
                    ExcludedQuakeMaximumScales = [JmaScale.Two, JmaScale.FiveUpper],
                    QuakePrefectureCodes = ["13", "14"], QuakePrefectureMode = mode,
                },
                Display = defaults.Display with { ShowLowIntensityPointsInStrongQuakes = true },
            });
            AppSettings actual = await store.LoadAsync();
            Assert.HasCount(2, actual.Filter.ExcludedQuakeMaximumScales!);
            Assert.HasCount(2, actual.Filter.QuakePrefectureCodes);
            Assert.AreEqual(mode, actual.Filter.QuakePrefectureMode);
            Assert.IsTrue(actual.Display.ShowLowIntensityPointsInStrongQuakes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
