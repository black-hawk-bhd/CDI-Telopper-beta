using System.IO;
using System.Reflection;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Audio;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Operations;
using EEWTelop.Application.Persistence;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.Diagnostics;
#if QTELOPPER_AXIS_PROVIDER
using EEWTelop.Infrastructure.Axis.Configuration;
using EEWTelop.Infrastructure.Axis.Normalization;
using EEWTelop.Infrastructure.Axis.Security;
using EEWTelop.Infrastructure.Axis.Transport;
#endif
using EEWTelop.Infrastructure.Identity;
using EEWTelop.Infrastructure.Logging;
#if QTELOPPER_DMDATA_PROVIDER
using EEWTelop.Infrastructure.Dmdata.Configuration;
using EEWTelop.Infrastructure.Dmdata.Normalization;
using EEWTelop.Infrastructure.Dmdata.Transport;
#endif
using EEWTelop.Infrastructure.P2P.Configuration;
using EEWTelop.Infrastructure.P2P.Normalization;
using EEWTelop.Infrastructure.P2P.Transport;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Infrastructure.Operations;
using EEWTelop.Infrastructure.Time;

namespace EEWTelop.Wpf.Bootstrap;

public static class AppComposition
{
    public static AppServices CreateDefault(string? dataDirectory = null)
    {
        var clock = new SystemClock();
        string applicationDataDirectory = ResolveDataDirectory(dataDirectory);
        var uiLogs = new UiLogBuffer();
        var fileLogs = new FileAppLogWriter(Path.Combine(applicationDataDirectory, "logs"));
        var logWriter = new CompositeAppLogWriter(uiLogs, fileLogs);
        var settingsStore = new JsonSettingsStore(
            Path.Combine(applicationDataDirectory, "settings.json"),
            logWriter);
        AppSettings settings = settingsStore.LoadAsync().AsTask().GetAwaiter().GetResult();
        ProviderSettings availableProviderSettings = NormalizeProviderAvailability(
            settings.Provider);
        if (availableProviderSettings != settings.Provider)
        {
            settings = settings with { Provider = availableProviderSettings };
        }
        var rawMessageArchive = new FileRawProviderMessageArchive(
            Path.Combine(applicationDataDirectory, "raw-reception"),
            settings.Log,
            logWriter);
        // The per-stage delivery timeline was retired because continuously tracing every
        // OBS route refresh caused unnecessary UI, disk and CPU load during operation.
        var testCaseLibrary = new FileTestCaseLibrary(
            Path.Combine(applicationDataDirectory, "test-library"));
        var stateStore = new JsonDisplayStateStore(
            Path.Combine(applicationDataDirectory, "state.json"),
            logWriter);
        ProviderOptions provider = ProviderOptions.FromSettings(settings.Provider);
        var signatureBuilder = new EventSignatureBuilder();
        var p2pNormalizer = new P2pEventNormalizer(signatureBuilder);
        var liveNormalizers = new List<KeyValuePair<string, IEventNormalizer>>
        {
            new("p2pquake", p2pNormalizer),
        };
#if QTELOPPER_DMDATA_PROVIDER
        // AXIS jmx-* and DMDATA.JP both carry JMA XML-derived telegrams.
        // Normalize them into the same domain events before any display logic runs.
        IEventNormalizer jmaXmlNormalizer = new JmaXmlEventNormalizer(signatureBuilder);
        if (!BuildFeatures.ExtendedFeaturesEnabled)
        {
            jmaXmlNormalizer = new EventKindFilteringEventNormalizer(
                jmaXmlNormalizer,
                [EventKind.Eew, EventKind.Quake, EventKind.Tsunami]);
        }
        // The isolated test library parses JMA XML in every build flavour,
        // even when a commercial provider is compiled out.
        liveNormalizers.Add(new(FileTestCaseLibrary.JmaXmlTestProviderName, jmaXmlNormalizer));
        if (BuildFeatures.DmdataProviderEnabled)
        {
            liveNormalizers.Add(new("dmdata.jp", jmaXmlNormalizer));
        }
        if (BuildFeatures.AxisProviderEnabled)
        {
#if QTELOPPER_AXIS_PROVIDER
            liveNormalizers.Add(new(
                AxisProviderOptions.ProviderName,
                new AxisEventNormalizer(jmaXmlNormalizer, signatureBuilder)));
#endif
        }
#endif
        var normalizer = new ProviderSelectionEventNormalizer(
            new ProviderRoutingEventNormalizer(liveNormalizers),
            settings.Provider);
        var pageComposer = new PageComposer();
        var displayCoordinator = new PriorityCoordinator(clock, settings.Display);
        DisplayStateDocument storedState = stateStore.LoadAsync().AsTask().GetAwaiter().GetResult();
        CoordinatorSnapshot restored = displayCoordinator.Restore(storedState.ToRestoreState(clock.UtcNow));
        if (restored.CurrentProgram is not null || restored.PersistentTsunami is not null)
        {
            logWriter.WriteAsync(new AppLogEntry(
                clock.UtcNow,
                AppLogLevel.Information,
                "StateRestored",
                "有効な本番表示状態をstate.jsonから復元しました。"))
                .AsTask().GetAwaiter().GetResult();
        }
        var p2pEventSource = new P2pEventSource(provider, clock, logWriter);
        var eventSources = new Dictionary<ReceptionProvider, IEventSource>
        {
            [ReceptionProvider.P2pQuake] = p2pEventSource,
        };
        if (BuildFeatures.DmdataProviderEnabled)
        {
#if QTELOPPER_DMDATA_PROVIDER
            DmdataProviderOptions dmdataOptions = DmdataProviderOptions.FromSettings(
                settings.Provider,
                BuildFeatures.ExtendedFeaturesEnabled);
            eventSources[ReceptionProvider.Dmdata] = new DmdataEventSource(
                dmdataOptions,
                clock,
                logWriter,
                BuildFeatures.ExtendedFeaturesEnabled);
#endif
        }
        if (BuildFeatures.AxisProviderEnabled)
        {
#if QTELOPPER_AXIS_PROVIDER
            var axisEventSource = new AxisEventSource(
                AxisProviderOptions.FromSettings(settings.Provider),
                clock,
                logWriter);
            eventSources[ReceptionProvider.Axis] = axisEventSource;
#endif
        }
        var eventSource = new RoutedProviderEventSource(settings.Provider, eventSources);
        var versionCache = new EventVersionCache();
        versionCache.Restore(storedState.RecentSignatures);
        var ingestionPipeline = new EventIngestionPipeline(
            normalizer,
            versionCache,
            pageComposer,
            displayCoordinator,
            settings.Display,
            settings.Filter);
        return new AppServices(
            Clock: clock,
            InitialSettings: settings,
            IdGenerator: new GuidIdGenerator(),
            SettingsStore: settingsStore,
            LogWriter: logWriter,
            UiLogs: uiLogs,
            Provider: provider,
            EventNormalizer: normalizer,
            PageComposer: pageComposer,
            DisplayCoordinator: displayCoordinator,
            EventSource: eventSource,
#if QTELOPPER_AXIS_PROVIDER
            AxisTokenRefreshService: BuildFeatures.AxisProviderEnabled
                ? new AxisTokenRefreshService(clock)
                : null,
#else
            AxisTokenRefreshService: null,
#endif
            IngestionPipeline: ingestionPipeline,
            ReceptionService: new EventReceptionService(
                eventSource,
                ingestionPipeline,
                rawMessageArchive),
            StateStore: stateStore,
            DiagnosticsWriter: new ZipDiagnosticsBundleWriter(),
            DataDirectory: applicationDataDirectory,
            VersionCache: versionCache,
            AudioPolicy: new AudioPolicy(),
            RawMessageArchive: rawMessageArchive,
            TestCaseLibrary: testCaseLibrary);
    }

    private static string ResolveDataDirectory(string? dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            return Path.GetFullPath(dataDirectory);
        }

        int versionMajor = typeof(AppComposition).Assembly.GetName().Version?.Major ?? 1;
        string dataEnvironmentVariable = versionMajor >= 2
            ? "QTELOPPER_V2_BETA_DATA_DIRECTORY"
            : "QTELOPPER_V1_DATA_DIRECTORY";
        string dataSeries = versionMajor >= 2 ? "2.x-beta" : "1.x";
        string? environmentDataDirectory = Environment.GetEnvironmentVariable(
            dataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentDataDirectory))
        {
            return Path.GetFullPath(environmentDataDirectory);
        }

        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.GetFullPath(Path.Combine(localApplicationData, "QTelopper", dataSeries));
    }

    private static ProviderSettings NormalizeProviderAvailability(ProviderSettings provider)
    {
        ReceptionProvider Normalize(ReceptionProvider selected) => selected switch
        {
            ReceptionProvider.Disabled => ReceptionProvider.Disabled,
            ReceptionProvider.Axis when BuildFeatures.AxisProviderEnabled =>
                ReceptionProvider.Axis,
            ReceptionProvider.Dmdata when BuildFeatures.DmdataProviderEnabled =>
                ReceptionProvider.Dmdata,
            ReceptionProvider.P2pQuake => ReceptionProvider.P2pQuake,
            _ => ReceptionProvider.P2pQuake,
        };

        ProviderRoutingSettings routing = provider.Routing with
        {
            Eew = Normalize(provider.Routing.Eew),
            Quake = Normalize(provider.Routing.Quake),
            Tsunami = Normalize(provider.Routing.Tsunami),
            Weather = Normalize(provider.Routing.Weather),
            Volcano = Normalize(provider.Routing.Volcano),
            NankaiTrough = Normalize(provider.Routing.NankaiTrough),
        };
        return provider with
        {
            Routing = routing,
            ReceptionProvider = routing.GetCompatibilityProvider(),
        };
    }
}

public sealed record AppServices(
    IClock Clock,
    AppSettings InitialSettings,
    IIdGenerator IdGenerator,
    ISettingsStore SettingsStore,
    IAppLogWriter LogWriter,
    UiLogBuffer UiLogs,
    ProviderOptions Provider,
    IEventNormalizer EventNormalizer,
    IPageComposer PageComposer,
    IDisplayCoordinator DisplayCoordinator,
    IEventSource EventSource,
    EventIngestionPipeline IngestionPipeline,
    EventReceptionService ReceptionService,
    IDisplayStateStore? StateStore = null,
    IAudioPolicy? AudioPolicy = null,
    IDiagnosticsBundleWriter? DiagnosticsWriter = null,
    string DataDirectory = "",
    IEventVersionCache? VersionCache = null,
    IHistoryRehearsalLoader? HistoryRehearsalLoader = null,
    IRawProviderMessageArchive? RawMessageArchive = null,
    IOperationalAlertCenter? OperationalAlerts = null,
    ISourceComparisonService? SourceComparison = null,
    ISettingsProfileStore? ProfileStore = null,
    ITestCaseLibrary? TestCaseLibrary = null,
    IAxisTokenRefreshService? AxisTokenRefreshService = null) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await EventSource.DisposeAsync().ConfigureAwait(false);
        if (AxisTokenRefreshService is IDisposable tokenRefreshDisposable)
        {
            tokenRefreshDisposable.Dispose();
        }
        if (HistoryRehearsalLoader is not null)
        {
            await HistoryRehearsalLoader.DisposeAsync().ConfigureAwait(false);
        }
        if (RawMessageArchive is IDisposable disposableRawArchive)
        {
            disposableRawArchive.Dispose();
        }
        if (LogWriter is IDisposable disposableLogWriter)
        {
            disposableLogWriter.Dispose();
        }
    }
}
