using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.P2P.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.P2P.Tests;

[TestClass]
public sealed class ProviderOptionsTests
{
    [TestMethod]
    public void ProductionUsesOfficialEndpoints()
    {
        ProviderOptions options = ProviderOptions.Production;

        Assert.AreEqual("wss://api.p2pquake.net/v2/ws", options.WebSocketUri.ToString());
        Assert.AreEqual("https://api.p2pquake.net/v2", options.RestBaseUri.ToString().TrimEnd('/'));
        Assert.IsEmpty(options.Validate());
    }

    [TestMethod]
    public void ValidateRejectsUnsafeSchemes()
    {
        var options = new ProviderOptions(
            ProviderMode.Custom,
            new Uri("https://example.test/socket"),
            new Uri("file:///C:/temp/api"));

        IReadOnlyList<string> errors = options.Validate();

        Assert.HasCount(2, errors);
    }
}
