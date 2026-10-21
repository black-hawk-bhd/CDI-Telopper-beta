using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Bootstrap;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class HistoryCompositionTests
{
    [TestMethod]
    public async Task RealCompositionLoadsJmaHistoryDespiteDifferentLiveProviderWithoutStartingReception()
    {
        if (!BuildFeatures.DmdataProviderEnabled || !BuildFeatures.ExtendedFeaturesEnabled) return;
        string directory = Path.Combine(Path.GetTempPath(), "cdi-history-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "history.xml");
        try
        {
            await File.WriteAllTextAsync(path, """
                <Report><Control><Title>震度速報</Title><Status>通常</Status><PublishingOffice>気象庁</PublishingOffice></Control>
                <Head><Title>震度速報</Title><ReportDateTime>2026-10-21T12:00:00+09:00</ReportDateTime>
                  <EventID>history-composition</EventID><InfoType>発表</InfoType></Head>
                <Body><Intensity><Observation><MaxInt>3</MaxInt><Pref><Name>東京都</Name>
                  <Area><Name>東京都２３区</Name><Code>350</Code><MaxInt>3</MaxInt></Area>
                </Pref></Observation></Intensity></Body></Report>
                """);
            await using AppServices services = AppComposition.CreateDefault(directory);
            Assert.IsNotNull(services.HistoryRehearsalLoader);
            Assert.AreEqual(ReceptionProvider.P2pQuake, services.InitialSettings.Provider.ReceptionProvider);
            var loaded = await services.HistoryRehearsalLoader.LoadAsync(services.InitialSettings.History with
                { Api = HistoryApi.LocalJmaXml, LocalXmlFilePath = path }, services.InitialSettings.Provider);
            Assert.HasCount(1, loaded.Events);
            Assert.AreEqual(SourceMode.HistoryRehearsal, loaded.Events[0].SourceMode);
            Assert.AreEqual(0, loaded.InvalidCount);
            Assert.IsNull(services.DisplayCoordinator.Evaluate().CurrentProgram);
            var program = services.PageComposer.Compose(loaded.Events[0], services.InitialSettings.Display);
            Assert.IsTrue(program.Pages.Any(static page => page.AccessibleText.Contains("東京都", StringComparison.Ordinal)));
        }
        finally
        {
            // Only the GUID-named directory created by this test, never application/user storage.
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (Path.GetFullPath(directory).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, recursive: true);
        }
    }
}
