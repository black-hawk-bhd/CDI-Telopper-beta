using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class DmdataProviderOptionsTests
{
    private static readonly string[] StableClassifications =
        ["eew.warning", "telegram.earthquake"];

    private static readonly string[] QuakeTelegramTypes =
        ["VXSE51", "VXSE52", "VXSE53", "VXSE62", "VYSE60"];

    private static readonly string[] EarthquakeClassification =
        ["telegram.earthquake"];

    [TestMethod]
    public void NewSettingsDoNotRequestAnyUnselectedContractCategory()
    {
        ProviderSettings settings = AppSettings.CreateDefault().Provider;

        DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

        Assert.AreEqual(0, options.Classifications.Count);
        Assert.AreEqual(0, options.TelegramTypes.Count);
        Assert.IsTrue(options.Validate().Any(static value =>
            value.Contains("contract category", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void StablePolicyLimitsSubscriptionToEewEarthquakeAndTsunami()
    {
        ProviderSettings settings = AppSettings.CreateDefault().Provider with
        {
            DmdataReceiveEewWarnings = true,
            DmdataReceiveEarthquakeTelegrams = true,
            DmdataReceiveWeatherWarnings = true,
            DmdataReceiveVolcanoTelegrams = true,
            DmdataUseLegacyWeatherWarningTelegrams = true,
        };

        DmdataProviderOptions options = DmdataProviderOptions.FromSettings(
            settings,
            allowExtendedCategories: false);

        CollectionAssert.AreEquivalent(
            StableClassifications,
            options.Classifications.ToArray());
        CollectionAssert.Contains(options.TelegramTypes.ToArray(), "VXSE43");
        CollectionAssert.DoesNotContain(options.TelegramTypes.ToArray(), "VXSE45");
        CollectionAssert.Contains(options.TelegramTypes.ToArray(), "VXSE53");
        CollectionAssert.Contains(options.TelegramTypes.ToArray(), "VYSE60");
        CollectionAssert.Contains(options.TelegramTypes.ToArray(), "VYSE50");
        CollectionAssert.Contains(options.TelegramTypes.ToArray(), "VTSE41");
        Assert.IsFalse(options.TelegramTypes.Any(static value =>
            value.StartsWith("VP", StringComparison.Ordinal) ||
            value.StartsWith("VF", StringComparison.Ordinal)));
        Assert.IsFalse(options.ReceiveWeatherWarnings);
        Assert.IsFalse(options.ReceiveVolcanoTelegrams);
        Assert.IsFalse(options.UseLegacyWeatherWarningTelegrams);
    }

    [TestMethod]
    public void EewWarningContractRequestsEewWarningAndVxse43Only()
    {
        ProviderSettings settings = AppSettings.CreateDefault().Provider with
        {
            DmdataReceiveEewWarnings = true,
        };

        DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

        Assert.HasCount(1, options.Classifications);
        Assert.AreEqual("eew.warning", options.Classifications[0]);
        Assert.HasCount(1, options.TelegramTypes);
        Assert.AreEqual("VXSE43", options.TelegramTypes[0]);
    }

    [TestMethod]
    public void EewForecastContractRequestsEewForecastAndVxse45Only()
    {
        ProviderSettings settings = AppSettings.CreateDefault().Provider with
        {
            DmdataReceiveEewWarnings = true,
            DmdataEewContractType = DmdataEewContractType.Forecast,
        };

        DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

        Assert.HasCount(1, options.Classifications);
        Assert.AreEqual("eew.forecast", options.Classifications[0]);
        Assert.HasCount(1, options.TelegramTypes);
        Assert.AreEqual("VXSE45", options.TelegramTypes[0]);
    }

    [TestMethod]
    public void RoutingRequestsOnlyTheSelectedDmdataTelegramFamilies()
    {
        ProviderSettings settings = AppSettings.CreateDefault().Provider with
        {
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Disabled) with
            {
                Quake = ReceptionProvider.Dmdata,
            },
            // Stale schema-24 flags must not broaden a route selected by the operator.
            DmdataReceiveEewWarnings = true,
            DmdataReceiveEarthquakeTelegrams = true,
            DmdataReceiveWeatherWarnings = true,
            DmdataReceiveVolcanoTelegrams = true,
        };

        DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

        CollectionAssert.AreEqual(
            QuakeTelegramTypes,
            options.TelegramTypes.ToArray());
        CollectionAssert.AreEqual(
            EarthquakeClassification,
            options.Classifications.ToArray());
        CollectionAssert.DoesNotContain(options.TelegramTypes.ToArray(), "VXSE45");
        CollectionAssert.DoesNotContain(options.TelegramTypes.ToArray(), "VXSE43");
        CollectionAssert.DoesNotContain(options.TelegramTypes.ToArray(), "VYSE50");
        CollectionAssert.DoesNotContain(options.TelegramTypes.ToArray(), "VTSE41");
        Assert.IsFalse(options.ReceiveWeatherWarnings);
        Assert.IsFalse(options.ReceiveVolcanoTelegrams);
    }

    [TestMethod]
    public void CurrentUserProtectedCredentialTakesPriorityOverLegacyEnvironmentVariable()
    {
        string variable = "QTELOPPER_DMDATA_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "legacy-secret");
        try
        {
            string protectedCredential = DmdataCredentialProtector.Protect("direct-secret");
            Assert.IsFalse(string.IsNullOrWhiteSpace(protectedCredential));
            Assert.AreNotEqual("direct-secret", protectedCredential);

            ProviderSettings settings = AppSettings.CreateDefault().Provider with
            {
                DmdataProtectedCredential = protectedCredential,
                DmdataCredentialEnvironmentVariable = variable,
            };

            DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

            Assert.AreEqual("direct-secret", options.Credential);
            Assert.AreEqual("direct-secret", DmdataCredentialProtector.Unprotect(
                protectedCredential));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public void LegacyEnvironmentVariableIsUsedOnlyWhenProtectedCredentialIsMissing()
    {
        string variable = "QTELOPPER_DMDATA_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "legacy-secret");
        try
        {
            ProviderSettings settings = AppSettings.CreateDefault().Provider with
            {
                DmdataProtectedCredential = string.Empty,
                DmdataCredentialEnvironmentVariable = variable,
            };

            DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

            Assert.AreEqual("legacy-secret", options.Credential);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public void Vpbs51IsSubscribedInCurrentAndLegacyWeatherModes()
    {
        foreach (bool useLegacyWeatherWarnings in new[] { false, true })
        {
            ProviderSettings settings = AppSettings.CreateDefault().Provider with
            {
                DmdataReceiveWeatherWarnings = true,
                DmdataUseLegacyWeatherWarningTelegrams = useLegacyWeatherWarnings,
            };

            DmdataProviderOptions options = DmdataProviderOptions.FromSettings(settings);

            CollectionAssert.Contains(options.TelegramTypes.ToArray(), "VPBS51");
        }
    }
}
