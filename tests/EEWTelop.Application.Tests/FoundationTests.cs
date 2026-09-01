using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Identity;
using EEWTelop.Infrastructure.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Application.Tests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public void DefaultSettingsMatchPhaseOneBaseline()
    {
        AppSettings settings = AppSettings.CreateDefault();

        Assert.AreEqual(4.0, settings.Display.PageDurationSeconds);
        Assert.AreEqual(1920, settings.Display.Width);
        Assert.AreEqual(1080, settings.Display.Height);
        Assert.AreEqual(BackgroundMode.Transparent, settings.Display.BackgroundMode);
        Assert.IsTrue(settings.Safety.ConfirmTestInProduction);
        Assert.IsFalse(settings.Safety.RestoreRehearsalState);
        Assert.IsTrue(settings.Filter.Eew);
        Assert.IsFalse(settings.Filter.HideQuakeBelowIntensity3);
        Assert.IsTrue(settings.Obs.RuntimeRecovery);
        Assert.AreEqual(250, settings.Log.UiMaxEntries);
        Assert.IsFalse(settings.Compatibility.EnrichQuakeById);
    }

    [TestMethod]
    public async Task SettingsStoreRoundTripsImmutableSettings()
    {
        var store = new InMemorySettingsStore();
        AppSettings changed = AppSettings.CreateDefault() with
        {
            Display = AppSettings.CreateDefault().Display with { FontScale = 1.2 },
        };

        await store.SaveAsync(changed);
        AppSettings loaded = await store.LoadAsync();

        Assert.AreEqual(1.2, loaded.Display.FontScale);
    }

    [TestMethod]
    public void IdGeneratorReturnsUniqueCompactIds()
    {
        var generator = new GuidIdGenerator();

        string first = generator.NewId();
        string second = generator.NewId();

        Assert.AreEqual(32, first.Length);
        Assert.AreNotEqual(first, second);
    }
}
