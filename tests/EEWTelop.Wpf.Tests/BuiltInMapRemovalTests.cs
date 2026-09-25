using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class BuiltInMapRemovalTests
{
    [TestMethod]
    public void BuiltInMapTypesAssetsAndBuildSwitchAreAbsent()
    {
        var assembly = typeof(BuildFeatures).Assembly;
        Assert.IsNull(typeof(BuildFeatures).GetProperty("TrialMapEnabled"));
        Assert.IsNull(assembly.GetType("EEWTelop.Wpf.MapReviewWindow"));
        Assert.IsNull(assembly.GetType("EEWTelop.Wpf.Controls.TrialQuakeMap"));
        Assert.IsFalse(assembly.GetManifestResourceNames().Any(name =>
            name.Contains("trial-seismic", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("trial-municipality", StringComparison.OrdinalIgnoreCase)));
        Assert.IsNotNull(assembly.GetType("EEWTelop.Wpf.Obs.ExternalApiState"));
    }
}
