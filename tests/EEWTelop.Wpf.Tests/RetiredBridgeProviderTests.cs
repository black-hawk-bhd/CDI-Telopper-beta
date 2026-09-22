using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class RetiredBridgeProviderTests
{
    [TestMethod]
    public void RetiredProviderBecomesDisabledWithoutChangingOtherSources()
    {
        var settings = AppSettings.CreateDefault();
        settings = settings with
        {
            Provider = settings.Provider with
            {
                ReceptionProvider = ReceptionProvider.ObsEarthquakeBridge,
                Routing = settings.Provider.Routing with
                {
                    Eew = ReceptionProvider.ObsEarthquakeBridge,
                    Quake = ReceptionProvider.P2pQuake,
                    Tsunami = ReceptionProvider.ObsEarthquakeBridge,
                },
            },
        };
        var result = JsonSettingsStore.NormalizeDocument(settings);
        Assert.AreEqual(ReceptionProvider.Disabled, result.Provider.Routing.Eew);
        Assert.AreEqual(ReceptionProvider.Disabled, result.Provider.Routing.Tsunami);
        Assert.AreEqual(ReceptionProvider.P2pQuake, result.Provider.Routing.Quake);
        Assert.IsFalse(result.Provider.Routing.Uses(ReceptionProvider.ObsEarthquakeBridge));
    }
}
