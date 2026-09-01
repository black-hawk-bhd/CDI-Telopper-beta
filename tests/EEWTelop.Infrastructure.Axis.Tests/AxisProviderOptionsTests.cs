using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Axis.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Axis.Tests;

[TestClass]
public sealed class AxisProviderOptionsTests
{
    [TestMethod]
    public void JmxSeismologyIsAccepted()
    {
        var options = new AxisProviderOptions(
            new Uri("https://axis.prioris.jp/api/"),
            "test-token",
            AxisProviderOptions.SeismologyChannel);

        Assert.IsEmpty(options.Validate());
    }

    [TestMethod]
    public void JmxMeteorologyIsAccepted()
    {
        var options = new AxisProviderOptions(
            new Uri("https://axis.prioris.jp/api/"),
            "test-token",
            AxisProviderOptions.MeteorologyChannel);

        Assert.IsEmpty(options.Validate());
    }

    [TestMethod]
    public void DefaultConfigurationAcceptsJmaAndEewChannels()
    {
        var options = new AxisProviderOptions(
            new Uri("https://axis.prioris.jp/api/"),
            "test-token",
            AxisProviderOptions.DefaultChannel);

        Assert.IsEmpty(options.Validate());
        Assert.IsTrue(options.AcceptsChannel(AxisProviderOptions.SeismologyChannel));
        Assert.IsTrue(options.AcceptsChannel(AxisProviderOptions.MeteorologyChannel));
        Assert.IsTrue(options.AcceptsChannel(AxisProviderOptions.EewChannel));
    }

    [TestMethod]
    public void EewChannelIsAccepted()
    {
        var options = new AxisProviderOptions(
            new Uri("https://axis.prioris.jp/api/"),
            "test-token",
            AxisProviderOptions.EewChannel);

        Assert.IsEmpty(options.Validate());
    }

    [TestMethod]
    public void RoutingBuildsOnlyTheSelectedAxisChannels()
    {
        ProviderRoutingSettings routing = ProviderRoutingSettings.FromLegacy(
            ReceptionProvider.Disabled) with
        {
            Eew = ReceptionProvider.Axis,
            Weather = ReceptionProvider.Axis,
        };

        string actual = AxisProviderOptions.BuildSelectedChannels(routing);

        Assert.AreEqual(
            $"{AxisProviderOptions.MeteorologyChannel},{AxisProviderOptions.EewChannel}",
            actual);
        Assert.DoesNotContain(AxisProviderOptions.SeismologyChannel, actual);
        Assert.DoesNotContain(AxisProviderOptions.VolcanologyChannel, actual);
    }

    [TestMethod]
    public void AllDisabledRoutingBuildsNoAxisChannel()
    {
        ProviderRoutingSettings routing = ProviderRoutingSettings.FromLegacy(
            ReceptionProvider.Disabled);

        Assert.AreEqual(string.Empty, AxisProviderOptions.BuildSelectedChannels(routing));
    }
}
