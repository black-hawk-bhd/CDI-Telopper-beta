using System.IO;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.Bootstrap;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class DataDirectorySelectionTests
{
    [TestMethod]
    public async Task RestartUsesSettingsFolderForConfigurationAndReceivedXmlAndJson()
    {
        using var directories = new TestDirectories();
        DataDirectorySelection initial = directories.CreateSelection();
        Assert.AreEqual(directories.AppData, initial.ActiveDirectory);

        AppSettings defaults = AppSettings.CreateDefault();
        var initialStore = new JsonSettingsStore(Path.Combine(directories.AppData, "settings.json"), new UiLogBuffer());
        await initialStore.SaveAsync(defaults with { Log = defaults.Log with { SaveRawProviderMessages = true } });
        string oldArchive = Path.Combine(directories.AppData, "raw-reception");
        Directory.CreateDirectory(oldArchive);
        File.WriteAllText(Path.Combine(oldArchive, "existing.json"), "{}");

        await initial.SelectForNextLaunchAsync(usePortable: true);
        Assert.AreEqual(directories.AppData, initial.ActiveDirectory);

        DataDirectorySelection restarted = directories.CreateSelection();
        Assert.AreEqual(directories.Portable, restarted.ActiveDirectory);
        await using AppServices services = AppComposition.CreateForStorage(restarted);
        Assert.IsTrue(services.InitialSettings.Log.SaveRawProviderMessages);
        await services.SettingsStore.SaveAsync(services.InitialSettings);

        IRawProviderMessageArchive? archive = services.RawMessageArchive;
        Assert.IsNotNull(archive);
        const string xml = "<Report><Control/><Head/></Report>";
        const string json = "{\"channel\":\"jmx-seismology\"}";
        await archive.SaveAsync(new RawProviderMessage("axis", xml, SourceMode.Production, DateTimeOffset.UtcNow)
        {
            ContentFormat = RawProviderContentFormat.JmaXml,
            TransportPayload = json,
            TransportContentFormat = RawProviderContentFormat.Json,
        });

        string newArchive = Path.Combine(directories.Portable, "raw-reception");
        Assert.AreEqual(xml, File.ReadAllText(Directory.GetFiles(newArchive, "*.xml").Single()));
        Assert.AreEqual(json, File.ReadAllText(Directory.GetFiles(newArchive, "*.json").Single()));
        Assert.HasCount(1, Directory.GetFiles(oldArchive));
        Assert.IsTrue(File.Exists(Path.Combine(directories.Portable, "settings.json")));
    }

    [TestMethod]
    public async Task ExistingSettingsAreKeptWhenSwitchingInEitherDirection()
    {
        using var directories = new TestDirectories();
        Directory.CreateDirectory(directories.AppData);
        Directory.CreateDirectory(directories.Portable);
        string appDataSettings = Path.Combine(directories.AppData, "settings.json");
        string portableSettings = Path.Combine(directories.Portable, "settings.json");
        File.WriteAllText(appDataSettings, "appdata");
        File.WriteAllText(portableSettings, "portable");

        await directories.CreateSelection().SelectForNextLaunchAsync(usePortable: true);
        DataDirectorySelection portable = directories.CreateSelection();
        Assert.AreEqual(directories.Portable, portable.ActiveDirectory);
        Assert.AreEqual("portable", File.ReadAllText(portableSettings));

        var forced = new DataDirectorySelection(directories.AppData, directories.Program, directories.Root);
        Assert.AreEqual(directories.Root, forced.ActiveDirectory);
        Assert.IsFalse(forced.CanChange);

        await portable.SelectForNextLaunchAsync(usePortable: false);
        Assert.AreEqual(directories.AppData, directories.CreateSelection().ActiveDirectory);
        Assert.AreEqual("appdata", File.ReadAllText(appDataSettings));
        Assert.AreEqual("portable", File.ReadAllText(portableSettings));
    }

    [TestMethod]
    public async Task AnUnavailableSettingsDirectoryDoesNotChangeTheStartupLocation()
    {
        using var directories = new TestDirectories();
        Directory.CreateDirectory(directories.Program);
        File.WriteAllText(directories.Portable, "already a file");

        await Assert.ThrowsAsync<IOException>(
            () => directories.CreateSelection().SelectForNextLaunchAsync(usePortable: true));

        Assert.AreEqual(directories.AppData, directories.CreateSelection().ActiveDirectory);
    }

    private sealed class TestDirectories : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cdi-storage-tests-");

        public string Root => _root.FullName;
        public string Program => Path.Combine(Root, "program");
        public string AppData => Path.Combine(Root, "appdata");
        public string Portable => Path.Combine(Program, "Settings");

        public DataDirectorySelection CreateSelection() => new(AppData, Program);

        public void Dispose() => _root.Delete(recursive: true);
    }
}
