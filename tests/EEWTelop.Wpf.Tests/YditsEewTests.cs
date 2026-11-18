using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Infrastructure.Ydits;
using EEWTelop.Wpf.Bootstrap;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class YditsEewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 12, TimeSpan.FromHours(9));
    private static readonly string[] FailoverHosts =
        ["api.ydits.net", "api2.ydits.net", "api3.ydits.net", "api3.ydits.net", "api3.ydits.net"];
    private const string Warning = """
        {"status":"OK","time":"2026-11-11T12:00:12.000+09:00","isEew":true,"report":{
            "type":"normal","time":"2026-11-11T12:00:10.000+09:00","isLast":false,
            "isFinal":false,"isCanceled":false,"isWarning":true,"earthquake":{
                "originTime":"2026-11-11T12:00:00.000+09:00","arrivalTime":"2026-11-11T12:00:05.000+09:00",
                "condition":false,"hypocenterName":"茨城県沖","intensity":"5-","magnitude":6.1,"depth":40
            }
        }}
        """;
    private const string Cancellation = """
        {"status":"OK","time":"2026-11-11T12:00:12.000+09:00","isEew":false,"report":{
            "type":"normal","time":"2026-11-11T12:00:11.000+09:00","isLast":true,
            "isFinal":false,"isCanceled":true,"isWarning":false
        }}
        """;

    [TestMethod]
    [DataRow(ReceptionProvider.Ydits)]
    [DataRow(ReceptionProvider.YditsTest)]
    public async Task YditsAvailabilityAppliesToOptionsAndSavedRoutes(ReceptionProvider provider)
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var routing = defaults.Provider.Routing with { Eew = provider, Quake = ReceptionProvider.Wolfx };
        bool enabled = BuildFeatures.YditsProviderEnabled;
        ProviderRoutingSettings availableRouting = enabled ? routing : routing with { Eew = ReceptionProvider.Disabled };
        var previous = defaults with
        {
            Provider = defaults.Provider with { Routing = routing, ReceptionProvider = routing.GetCompatibilityProvider() },
        };
        var editor = new SettingsEditorViewModel(previous);
        Assert.AreEqual(enabled, editor.ReceptionProviders.Contains(provider));
        Assert.AreEqual(enabled, editor.EewAndQuakeProviderOptions.Any(option => option.Value == provider));
        editor.EewProvider = provider;
        Assert.AreEqual(availableRouting.Eew, editor.EewProvider);
        Assert.AreEqual(availableRouting, editor.ToSettings(defaults).Provider.Routing);

        string directory = Path.Combine(Path.GetTempPath(), $"cdi-suspended-ydits-{Guid.NewGuid():N}");
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"), new UiLogBuffer());
            await store.SaveAsync(previous);
            await using AppServices services = AppComposition.CreateDefault(directory);
            Assert.AreEqual(availableRouting, services.InitialSettings.Provider.Routing);
            Assert.AreEqual(!enabled, services.UiLogs.GetSnapshot().Any(entry =>
                entry.EventName == "YditsReceptionSuspended"));
            var source = Assert.IsInstanceOfType<RoutedProviderEventSource>(services.EventSource);
            Assert.AreEqual(enabled && provider == ReceptionProvider.Ydits, source.GetDataConnection(EventKind.Eew).Configured);
            Assert.IsTrue(source.GetDataConnection(EventKind.Quake).Configured);
            Assert.AreEqual(provider, (await store.LoadAsync()).Provider.Routing.Eew);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Vxse43RoutesToWarningDisplayAndUsesDetectionTimeAcrossUpdates()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        var normalizer = new YditsEventNormalizer(new EventSignatureBuilder());
        var routed = new ProviderSelectionEventNormalizer(normalizer, defaults.Provider);
        Assert.AreEqual(NormalizeStatus.Ignored, routed.Normalize(Raw(Warning)).Status);
        routed.UpdateProviderSelection(defaults.Provider with
        {
            Routing = defaults.Provider.Routing with { Eew = ReceptionProvider.Ydits },
        });
        var first = Assert.IsInstanceOfType<EewEvent>(routed.Normalize(Raw(Warning)).Event);
        Assert.AreEqual("VXSE43", first.Issue.RawType);
        Assert.IsTrue(first.IsWarning);
        Assert.AreEqual(JmaScale.FiveLower, first.Earthquake!.MaximumScale);
        Assert.AreEqual(6.1, first.Earthquake.Hypocenter!.Magnitude);
        Assert.AreEqual(40, first.Earthquake.Hypocenter.DepthKilometers);
        var program = new PageComposer().Compose(first, defaults.Display);
        StringAssert.Contains(program.Pages[0].AccessibleText, "茨城県沖で地震");

        string final = Warning.Replace("12:00:10", "12:00:11", StringComparison.Ordinal)
            .Replace("12:00:00", "11:59:59", StringComparison.Ordinal)
            .Replace("\"isLast\":false", "\"isLast\":true", StringComparison.Ordinal)
            .Replace("\"isFinal\":false", "\"isFinal\":true", StringComparison.Ordinal);
        var last = Assert.IsInstanceOfType<EewEvent>(routed.Normalize(Raw(final)).Event);
        Assert.IsTrue(last.IsFinal);
        Assert.AreEqual(first.Id, last.Id);
        Assert.AreNotEqual(first.Signature, last.Signature);
    }

    [TestMethod]
    public void TestReportsInactiveAndStaleReportsAndUncorrelatedCancellationsAreIgnored()
    {
        var normalizer = new YditsEventNormalizer(new EventSignatureBuilder());
        foreach (string payload in new[]
        {
            """{"status":"OK","isEew":false,"report":null}""",
            Warning.Replace("\"isEew\":true", "\"isEew\":false", StringComparison.Ordinal),
            Warning.Replace("\"normal\"", "\"drill\"", StringComparison.Ordinal),
            Warning.Replace("\"normal\"", "\"test\"", StringComparison.Ordinal),
            Warning.Replace("12:00:10", "11:57:10", StringComparison.Ordinal),
            Warning.Replace("12:00:10", "12:01:10", StringComparison.Ordinal),
            Cancellation,
        })
        {
            Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(Raw(payload)).Status);
        }
        Assert.AreEqual(NormalizeStatus.Invalid, normalizer.Normalize(Raw("""{"status":"OK","isEew":true}""")).Status);
    }

    [TestMethod]
    public async Task PollingFailsOverInOrderWithOneSecondBetweenRequestsAndRejectsDuplicateSnapshot()
    {
        string final = Warning.Replace("12:00:10", "12:00:11", StringComparison.Ordinal)
            .Replace("\"isLast\":false", "\"isLast\":true", StringComparison.Ordinal)
            .Replace("\"isFinal\":false", "\"isFinal\":true", StringComparison.Ordinal);
        using var handler = new ScriptedHandler(
            new(HttpStatusCode.InternalServerError),
            Reply("""{"status":"OK","isEew":true,"report":null}"""),
            Reply(Warning),
            Reply(Warning.Replace("12:00:12", "12:00:13", StringComparison.Ordinal)),
            Reply(final));
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var source = new YditsEventSource(new FixedClock(), http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var reader = source.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(Warning, reader.Current.Json);
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.AreEqual(final, reader.Current.Json);
        CollectionAssert.AreEqual(FailoverHosts, handler.Hosts);
        for (int i = 1; i < handler.Attempts.Count; i++)
            Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(1), Stopwatch.GetElapsedTime(handler.Attempts[i - 1], handler.Attempts[i]));
    }

    [TestMethod]
    public async Task RetryAfterAppliesBeforeFailover()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
        using var handler = new ScriptedHandler(limited);
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var source = new YditsEventSource(new FixedClock(), http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reconnect = new TaskCompletionSource<ProviderConnectionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ConnectionChanged += (_, state) =>
        {
            if (state.State == ProviderConnectionState.Reconnecting) reconnect.TrySetResult(state);
        };
        await using var reader = source.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        var result = await reconnect.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(TimeSpan.FromSeconds(45), result.RetryDelay);
        Assert.HasCount(1, handler.Hosts);
        await source.StopAsync();
        try { Assert.IsFalse(await pending); }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public async Task SandboxRoutingDoesNotPollYdits()
    {
        var settings = AppSettings.CreateDefault().Provider with
        {
            Mode = ProviderMode.Sandbox,
            ReceptionProvider = ReceptionProvider.Ydits,
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.Ydits),
        };
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var routed = new RoutedProviderEventSource(settings, new Dictionary<ReceptionProvider, IEventSource>
        {
            [ReceptionProvider.Ydits] = new YditsEventSource(new FixedClock(), http),
        });
        await using var reader = routed.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsFalse(await reader.MoveNextAsync());
        Assert.IsEmpty(handler.Hosts);
    }

    [TestMethod]
    public async Task TestSelectionDoesNotAdvertiseProductionEewHealth()
    {
        var settings = AppSettings.CreateDefault().Provider with
        {
            ReceptionProvider = ReceptionProvider.YditsTest,
            Routing = ProviderRoutingSettings.FromLegacy(ReceptionProvider.YditsTest),
        };
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var routed = new RoutedProviderEventSource(settings, new Dictionary<ReceptionProvider, IEventSource>
        {
            [ReceptionProvider.YditsTest] = new YditsEventSource(new FixedClock(), http),
        });
        Assert.IsFalse(routed.GetDataConnection(EventKind.Eew).Configured);
        Assert.HasCount(1, routed.GetProviderConnections());
    }

    [TestMethod]
    [DataRow("normal")]
    [DataRow("drill")]
    [DataRow("test")]
    public void OldTestSamplesArePreviewOnlyAndExcludedFromProductionApi(string type)
    {
        string sample = Warning.Replace("2026-11-11", "2024-03-04", StringComparison.Ordinal)
            .Replace("\"normal\"", $"\"{type}\"", StringComparison.Ordinal)
            .Replace("\"isEew\":true", "\"isEew\":false", StringComparison.Ordinal);
        var normalizer = new YditsEventNormalizer(new EventSignatureBuilder());
        var settings = AppSettings.CreateDefault();
        var selection = new ProviderSelectionEventNormalizer(normalizer, settings.Provider);
        var raw = new RawProviderMessage(YditsEventNormalizer.TestProviderName, sample, SourceMode.Sandbox, Now);
        Assert.AreEqual(NormalizeStatus.Ignored, selection.Normalize(raw).Status);
        selection.UpdateProviderSelection(settings.Provider with
        {
            Routing = settings.Provider.Routing with { Eew = ReceptionProvider.YditsTest },
        });
        var coordinator = new PriorityCoordinator(new FixedClock(), settings.Display);
        var pipeline = new EventIngestionPipeline(selection, new EventVersionCache(), new PageComposer(),
            coordinator, settings.Display) { HoldBeforeDisplay = true };
        var result = pipeline.Process(raw);
        Assert.AreEqual(EventIngestionStatus.Accepted, result.Status);
        var item = Assert.IsInstanceOfType<EewEvent>(result.Event);
        Assert.AreEqual(SourceMode.Sandbox, item.SourceMode);
        Assert.IsTrue(item.IsTest);
        Assert.AreEqual(2024, item.IssuedAt.Year);
        Assert.IsNull(result.Program);
        Assert.IsNull(result.Snapshot);
        Assert.IsFalse(result.AwaitingPreDisplayEdit);
        Assert.IsNotNull(result.ReviewProgram);
        StringAssert.Contains(result.ReviewProgram.Pages[0].AccessibleText, "茨城県沖で地震");
        Assert.AreEqual(EventIngestionStatus.Duplicate, pipeline.Process(raw).Status);

        var external = new ExternalEarthquakeState();
        external.Observe(result);
        var externalJson = JsonSerializer.SerializeToElement(external.ReadEew(Now));
        Assert.IsFalse(externalJson.GetProperty("hasInformation").GetBoolean());
        Assert.AreEqual(NormalizeStatus.Ignored, normalizer.Normalize(raw with { SourceMode = SourceMode.Production }).Status);
    }

    [TestMethod]
    public async Task TestEndpointRetriesOnlyItselfAndCannotDeduplicateProduction()
    {
        using var handler = new ScriptedHandler(new(HttpStatusCode.InternalServerError), Reply(Warning), Reply(Warning));
        using var http = new HttpClient(handler, disposeHandler: false);
        await using var source = new YditsEventSource(new FixedClock(TimeSpan.FromSeconds(11)), http);
        var settings = AppSettings.CreateDefault().Provider;
        source.ConfigureProvider(settings with { Routing = settings.Routing with { Eew = ReceptionProvider.YditsTest } });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var reader = source.ReadAllAsync(timeout.Token).GetAsyncEnumerator())
        {
            Assert.IsTrue(await reader.MoveNextAsync());
            Assert.AreEqual(YditsEventNormalizer.TestProviderName, reader.Current.Provider);
            Assert.AreEqual(SourceMode.Sandbox, reader.Current.SourceMode);
            Assert.HasCount(2, handler.Paths);
            Assert.IsTrue(handler.Paths.All(path => path == "/test/vxse43"));
        }

        source.ConfigureProvider(settings with { Routing = settings.Routing with { Eew = ReceptionProvider.Ydits } });
        await using var production = source.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.IsTrue(await production.MoveNextAsync());
        Assert.AreEqual(SourceMode.Production, production.Current.SourceMode);
        Assert.AreEqual("/vxse43", handler.Paths[2]);
    }

    private static RawProviderMessage Raw(string json) => new("ydits", json, SourceMode.Production, Now);
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class FixedClock(TimeSpan elapsedOffset = default) : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => Stopwatch.GetTimestamp();
        public TimeSpan GetElapsedTime(long startingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp) + elapsedOffset;
    }

    private sealed class ScriptedHandler(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        public List<string> Paths { get; } = [];
        public List<long> Attempts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Hosts.Add(request.RequestUri!.Host);
            Paths.Add(request.RequestUri.AbsolutePath);
            Attempts.Add(Stopwatch.GetTimestamp());
            return Task.FromResult(replies[Attempts.Count - 1]);
        }
    }
}
