using System.Text.Json;
using System.Windows.Media;
using EEWTelop.Wpf.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class TrialMapPaletteTests
{
    [TestMethod]
    public void DefaultUsesYellowGreenLandAndLightBlueSeaWithoutReplacingCustomColors()
    {
        Assert.AreEqual("#B5CF70", TrialMapPalette.Default.Colors["Land"]);
        Assert.AreEqual("#D3EDF5", TrialMapPalette.Default.Colors["Background"]);
        Assert.AreEqual("#B5D8E5", TrialMapPalette.Default.Colors["Grid"]);
        Assert.AreEqual("#F2EFE8", TrialMapPalette.Paper.Colors["Background"]);
        var custom = TrialMapPalette.Normalize(new(new() { ["Land"] = "#112233", ["Background"] = "#445566" }));
        Assert.AreEqual("#112233", custom.Colors["Land"]);
        Assert.AreEqual("#445566", custom.Colors["Background"]);
        TrialMapPalette.Paper.Colors["Land"] = "#000000";
        Assert.AreEqual("#B5CF70", TrialMapPalette.Default.Colors["Land"]);
    }

    [TestMethod]
    public void InvalidAndMissingColorsFallBackIndividually()
    {
        var palette = TrialMapPalette.Normalize(new(new() { ["Background"] = "#123456", ["Land"] = "transparent" }, true));
        Assert.AreEqual("#123456", palette.Colors["Background"]);
        Assert.AreEqual(TrialMapPalette.Default.Colors["Land"], palette.Colors["Land"]);
        Assert.AreEqual(TrialMapPalette.Fields.Length, palette.Colors.Count);
        Assert.IsTrue(palette.ShowGrid);
        Assert.IsFalse(TrialMapPalette.IsValid("#00FFFFFF"));
        Assert.IsFalse(TrialMapPalette.IsValid("#xyzxyz"));
        Assert.IsTrue(TrialMapPalette.IsValid("#aBcD09"));
        Assert.AreEqual(TrialMapPalette.Fields.Length, TrialMapPalette.Normalize(JsonSerializer.Deserialize<TrialMapPalette>("{\"Colors\":null}")).Colors.Count);
    }

    [TestMethod]
    public void MarkerContrastAndPresetsRemainIndependent()
    {
        Assert.AreEqual(Colors.White, TrialMapPalette.Contrast(Colors.Black).Color);
        Assert.AreEqual(Colors.Black, TrialMapPalette.Contrast(Colors.White).Color);
        var first = TrialMapPalette.Default;
        first.Colors["Background"] = "#123456";
        Assert.AreNotEqual(first.Colors["Background"], TrialMapPalette.Default.Colors["Background"]);
        Assert.AreNotEqual(TrialMapPalette.Default.Colors["Background"], TrialMapPalette.Dark.Colors["Background"]);
    }

    [TestMethod]
    public void PalettePersistsAndMalformedFileFallsBack()
    {
        string path = Path.Combine(Path.GetTempPath(), "cdi-map-palette-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.IsFalse(TrialMapPalette.Load(path).ShowGrid);
            var palette = TrialMapPalette.Dark with { ShowGrid = true };
            palette.Colors["50"] = "#ABCDEF";
            palette.Save(path);
            var restored = TrialMapPalette.Load(path);
            CollectionAssert.AreEquivalent(palette.Colors.ToArray(), restored.Colors.ToArray());
            Assert.IsTrue(restored.ShowGrid);
            File.WriteAllText(path, "invalid");
            Assert.AreEqual(TrialMapPalette.Default.Colors["Background"], TrialMapPalette.Load(path).Colors["Background"]);
        }
        finally { File.Delete(path); }
    }
}
