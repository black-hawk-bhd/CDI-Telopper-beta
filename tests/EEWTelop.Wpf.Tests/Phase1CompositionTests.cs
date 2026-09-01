using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.P2P.Normalization;
using EEWTelop.Wpf.Bootstrap;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class Phase1CompositionTests
{
    [TestMethod]
    public async Task CreateDefaultWiresSafeProductionDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"eewtelop-composition-{Guid.NewGuid():N}");
        try
        {
            await using AppServices services = AppComposition.CreateDefault(directory);
            AppSettings settings = await services.SettingsStore.LoadAsync();

            Assert.AreEqual(ProviderMode.Production, services.Provider.Mode);
            Assert.IsTrue(settings.Safety.ConfirmTestInProduction);
            Assert.IsFalse(settings.Safety.RestoreRehearsalState);
            Assert.IsNotEmpty(services.IdGenerator.NewId());
            Assert.IsNotNull(services.EventNormalizer);
            Assert.IsNotNull(services.PageComposer);
            Assert.IsNotNull(services.DisplayCoordinator);
            Assert.IsNotNull(services.EventSource);
            Assert.IsNotNull(services.IngestionPipeline);
            Assert.IsNotNull(services.ReceptionService);
            Assert.IsNotNull(services.StateStore);
            Assert.IsNotNull(services.AudioPolicy);
            Assert.AreEqual(AudioSettings.Disabled, services.InitialSettings.Audio);
            Assert.AreEqual(AudioSettings.Disabled, settings.Audio);
            Assert.IsNotNull(services.DiagnosticsWriter);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void WpfTypesDoNotExposeProviderDtos()
    {
        Type[] dtoTypes = typeof(P2pEventNormalizer).Assembly
            .GetTypes()
            .Where(static type => type.Namespace == "EEWTelop.Infrastructure.P2P.Dtos")
            .ToArray();
        HashSet<Type> dtoTypeSet = dtoTypes.ToHashSet();

        Type[] leakedTypes = typeof(AppComposition).Assembly
            .GetTypes()
            .Where(static type => type.Namespace?.StartsWith("EEWTelop.Wpf", StringComparison.Ordinal) == true)
            .SelectMany(static type => type.GetFields().Select(static field => field.FieldType)
                .Concat(type.GetProperties().Select(static property => property.PropertyType))
                .Concat(type.GetMethods().Select(static method => method.ReturnType))
                .Concat(type.GetMethods().SelectMany(static method =>
                    method.GetParameters().Select(static parameter => parameter.ParameterType))))
            .Where(dtoTypeSet.Contains)
            .Distinct()
            .ToArray();

        Assert.IsEmpty(leakedTypes, "A provider DTO reached the WPF type surface.");
    }
}
