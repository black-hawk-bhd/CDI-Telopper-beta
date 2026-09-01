using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Dmdata.History;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Infrastructure.Dmdata.Tests;

[TestClass]
public sealed class LocalJmaXmlHistoryMessageSourceTests
{
    [TestMethod]
    public async Task ReadsSelectedJmaXmlAsHistoryRehearsalMessage()
    {
        string path = CreateTemporaryXml("<Report xmlns=\"urn:jma\"><Control /></Report>");
        try
        {
            var source = new LocalJmaXmlHistoryMessageSource(new FakeClock());
            IReadOnlyList<RawProviderMessage> messages = await source.FetchAsync(
                CreateRequest(path));

            Assert.HasCount(1, messages);
            Assert.AreEqual(LocalJmaXmlHistoryMessageSource.ProviderName, messages[0].Provider);
            Assert.AreEqual(RawProviderContentFormat.JmaXml, messages[0].ContentFormat);
            Assert.AreEqual(SourceMode.HistoryRehearsal, messages[0].SourceMode);
            StringAssert.Contains(messages[0].Payload, "<Report");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RejectsXmlWhoseRootIsNotJmaReport()
    {
        string path = CreateTemporaryXml("<NotReport />");
        try
        {
            var source = new LocalJmaXmlHistoryMessageSource(new FakeClock());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => source.FetchAsync(CreateRequest(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RejectsDtdInExternalXml()
    {
        string path = CreateTemporaryXml("<!DOCTYPE Report [<!ENTITY x 'test'>]><Report>&x;</Report>");
        try
        {
            var source = new LocalJmaXmlHistoryMessageSource(new FakeClock());
            await Assert.ThrowsExactlyAsync<System.Xml.XmlException>(
                () => source.FetchAsync(CreateRequest(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HistoryFetchRequest CreateRequest(string path) => new(
        HistoryApi.LocalJmaXml,
        1,
        AppSettings.CreateDefault().Provider)
    {
        LocalXmlFilePath = path,
    };

    private static string CreateTemporaryXml(string xml)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"QTelopper-LocalJmaXml-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
