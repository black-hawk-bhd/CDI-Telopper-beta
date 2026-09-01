using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class RawProviderMessageArchiveTests
{
    [TestMethod]
    public async Task ArchiveIsOptInAndPreservesAxisJsonTogetherWithConvertedXml()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "QTelopper-raw-archive-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            LogSettings disabled = AppSettings.CreateDefault().Log;
            using var archive = new FileRawProviderMessageArchive(
                directory,
                disabled,
                new NullLog());
            var message = new RawProviderMessage(
                "axis",
                "<Report uuid=\"20260814064517_0_VFVO50_400000\"><Control/><Head><EventID>503</EventID></Head></Report>",
                SourceMode.Production,
                DateTimeOffset.UtcNow)
            {
                ContentFormat = RawProviderContentFormat.JmaXml,
                TransportPayload = "{\"channel\":\"jmx-volcanology\",\"message\":{\"uuid_\":\"20260814064517_0_VFVO50_400000\",\"Head\":{\"EventID\":\"503\"}}}",
                TransportContentFormat = RawProviderContentFormat.Json,
            };

            await archive.SaveAsync(message);
            Assert.IsFalse(Directory.Exists(directory));

            archive.Configure(disabled with { SaveRawProviderMessages = true });
            await archive.SaveAsync(message);

            string[] files = Directory.GetFiles(directory);
            Assert.HasCount(2, files);
            Assert.IsTrue(files.Any(static path =>
                path.EndsWith(".provider.xml", StringComparison.Ordinal)));
            Assert.IsTrue(files.Any(static path =>
                path.EndsWith(".transport.json", StringComparison.Ordinal)));
            string providerPath = files.Single(static path =>
                path.EndsWith(".provider.xml", StringComparison.Ordinal));
            string transportPath = files.Single(static path =>
                path.EndsWith(".transport.json", StringComparison.Ordinal));
            string providerName = Path.GetFileName(providerPath);
            string transportName = Path.GetFileName(transportPath);
            Assert.Contains("_jmx-volcanology_VFVO50_503_", providerName);
            Assert.AreEqual(
                providerName[..^".provider.xml".Length],
                transportName[..^".transport.json".Length]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class NullLog : IAppLogWriter
    {
        public ValueTask WriteAsync(
            AppLogEntry entry,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
