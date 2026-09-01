using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;
using System.Text.Json.Serialization;

namespace EEWTelop.Application.Operations;

public enum OperationalAlertSeverity
{
    Information = 0,
    Warning,
    Error,
}

public sealed record OperationalAlert(
    string Key,
    OperationalAlertSeverity Severity,
    string Title,
    string Message,
    DateTimeOffset RaisedAtUtc,
    bool IsRecovery = false)
{
    [JsonIgnore]
    public DateTimeOffset RaisedAtLocal => RaisedAtUtc.ToLocalTime();
}

public interface IOperationalAlertCenter
{
    event Action<OperationalAlert>? AlertRaised;

    IReadOnlyList<OperationalAlert> GetSnapshot();

    void Raise(OperationalAlert alert);

    void Recover(string key, string title, string message, DateTimeOffset nowUtc);
}

public sealed class OperationalAlertCenter : IOperationalAlertCenter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRaised =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private readonly Queue<OperationalAlert> _entries = new();
    private readonly TimeSpan _coalescing;

    public OperationalAlertCenter(TimeSpan coalescing) => _coalescing = coalescing;

    public event Action<OperationalAlert>? AlertRaised;

    public IReadOnlyList<OperationalAlert> GetSnapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Raise(OperationalAlert alert)
    {
        bool publish;
        lock (_gate)
        {
            publish = !_lastRaised.TryGetValue(alert.Key, out DateTimeOffset last) ||
                alert.RaisedAtUtc - last >= _coalescing;
            _active.Add(alert.Key);
            if (publish)
            {
                _lastRaised[alert.Key] = alert.RaisedAtUtc;
                Enqueue(alert);
            }
        }

        if (publish)
        {
            AlertRaised?.Invoke(alert);
        }
    }

    public void Recover(string key, string title, string message, DateTimeOffset nowUtc)
    {
        OperationalAlert? recovery = null;
        lock (_gate)
        {
            if (_active.Remove(key))
            {
                recovery = new OperationalAlert(
                    key,
                    OperationalAlertSeverity.Information,
                    title,
                    message,
                    nowUtc,
                    IsRecovery: true);
                Enqueue(recovery);
            }
        }

        if (recovery is not null)
        {
            AlertRaised?.Invoke(recovery);
        }
    }

    private void Enqueue(OperationalAlert entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > 250)
        {
            _entries.Dequeue();
        }
    }
}

public sealed record ProviderBranchConnectionSnapshot(
    string Name,
    ProviderConnectionSnapshot Connection);

public interface IProviderConnectionDiagnostics
{
    IReadOnlyList<ProviderBranchConnectionSnapshot> GetProviderConnections();
}

public enum SourceComparisonStatus
{
    Waiting = 0,
    Equal,
    Different,
    CounterpartMissing,
    NotComparable,
}

public sealed record SourceComparisonResult(
    string CorrelationKey,
    DateTimeOffset UpdatedAtUtc,
    SourceComparisonStatus Status,
    string ProviderA,
    string ProviderB,
    string Summary,
    IReadOnlyList<string> Differences);

public interface ISourceComparisonService
{
    event Action<SourceComparisonResult>? ResultUpdated;

    IReadOnlyList<SourceComparisonResult> GetSnapshot(DateTimeOffset nowUtc);

    void Observe(RawProviderMessage raw, EventIngestionResult result, DateTimeOffset nowUtc);

    void ObserveSelectedAudio(DisasterEvent disasterEvent, string audioCue, DateTimeOffset nowUtc) { }
}

public sealed class SourceComparisonService : ISourceComparisonService
{
    private readonly object _gate = new();
    private readonly TimeSpan _wait;
    private readonly Dictionary<string, Dictionary<string, SemanticObservation>> _observations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceComparisonResult> _results =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _providerLastObserved =
        new(StringComparer.OrdinalIgnoreCase);

    public SourceComparisonService(TimeSpan wait) => _wait = wait;

    public event Action<SourceComparisonResult>? ResultUpdated;

    public void Observe(RawProviderMessage raw, EventIngestionResult result, DateTimeOffset nowUtc)
    {
        if (result.Event is null || string.IsNullOrWhiteSpace(result.Event.Id.Value))
        {
            return;
        }

        string rawType = GetRawType(result.Event);
        string serial = GetSerial(result.Event);
        string key = string.Join("|", result.Event.Id.Value, rawType, serial);
        var observation = SemanticObservation.Create(raw.Provider, result, nowUtc);
        SourceComparisonResult updated;
        lock (_gate)
        {
            if (!_observations.TryGetValue(key, out Dictionary<string, SemanticObservation>? byProvider))
            {
                byProvider = new Dictionary<string, SemanticObservation>(StringComparer.OrdinalIgnoreCase);
                _observations[key] = byProvider;
            }
            byProvider[raw.Provider] = observation;
            _providerLastObserved[raw.Provider] = nowUtc;
            updated = BuildResult(key, byProvider, nowUtc);
            _results[key] = updated;
            Trim(nowUtc);
        }
        ResultUpdated?.Invoke(updated);
    }

    public void ObserveSelectedAudio(DisasterEvent disasterEvent, string audioCue, DateTimeOffset nowUtc)
    {
        string key = CreateCorrelationKey(disasterEvent);
        SourceComparisonResult? updated = null;
        lock (_gate)
        {
            if (!_observations.TryGetValue(key, out Dictionary<string, SemanticObservation>? byProvider)) return;
            string provider = disasterEvent.Provider;
            if (!byProvider.TryGetValue(provider, out SemanticObservation? observation))
            {
                KeyValuePair<string, SemanticObservation> candidate = byProvider.FirstOrDefault(pair =>
                    string.Equals(pair.Value.Provider, provider, StringComparison.OrdinalIgnoreCase));
                if (candidate.Value is null) return;
                provider = candidate.Key;
                observation = candidate.Value;
            }
            if (string.Equals(observation.AudioCue, audioCue, StringComparison.Ordinal)) return;
            byProvider[provider] = observation with { AudioCue = audioCue, ObservedAtUtc = nowUtc };
            updated = BuildResult(key, byProvider, nowUtc);
            _results[key] = updated;
        }
        if (updated is not null) ResultUpdated?.Invoke(updated);
    }

    public IReadOnlyList<SourceComparisonResult> GetSnapshot(DateTimeOffset nowUtc)
    {
        List<SourceComparisonResult> changed = [];
        SourceComparisonResult[] snapshot;
        lock (_gate)
        {
            foreach ((string key, Dictionary<string, SemanticObservation> value) in _observations)
            {
                SourceComparisonResult current = BuildResult(key, value, nowUtc);
                if (!_results.TryGetValue(key, out SourceComparisonResult? former) || !Equivalent(former, current))
                {
                    _results[key] = current;
                    changed.Add(current);
                }
            }
            Trim(nowUtc);
            snapshot = _results.Values.OrderByDescending(static item => item.UpdatedAtUtc)
                .Take(1000).ToArray();
        }
        foreach (SourceComparisonResult item in changed)
        {
            ResultUpdated?.Invoke(item);
        }
        return snapshot;
    }

    private SourceComparisonResult BuildResult(
        string key,
        Dictionary<string, SemanticObservation> observations,
        DateTimeOffset nowUtc)
    {
        SemanticObservation[] values = observations.Values
            .OrderBy(static item => item.Provider, StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length < 2)
        {
            bool hasOtherProvider = _providerLastObserved.Keys.Any(provider =>
                !string.Equals(provider, values[0].Provider, StringComparison.OrdinalIgnoreCase));
            if (!hasOtherProvider)
            {
                return new SourceComparisonResult(key, nowUtc, SourceComparisonStatus.NotComparable,
                    values[0].Provider, string.Empty, "比較可能な別ソースなし", []);
            }
            bool expired = nowUtc - values[0].ObservedAtUtc >= _wait;
            return new SourceComparisonResult(
                key,
                nowUtc,
                expired ? SourceComparisonStatus.CounterpartMissing : SourceComparisonStatus.Waiting,
                values[0].Provider,
                string.Empty,
                expired ? "比較対象未受信" : "比較対象待機中",
                []);
        }

        SemanticObservation left = values[0];
        List<string> differences = [];
        foreach (SemanticObservation right in values.Skip(1))
        {
            var pairDifferences = new List<string>();
            Compare("種別", left.Kind, right.Kind, pairDifferences);
            Compare("発表時刻", left.IssuedAtUtc, right.IssuedAtUtc, pairDifferences);
            Compare("状態", left.Status, right.Status, pairDifferences);
            Compare("取消", left.IsCancelled, right.IsCancelled, pairDifferences);
            Compare("訂正", left.IsCorrection, right.IsCorrection, pairDifferences);
            Compare("正規化内容", left.DomainFingerprint, right.DomainFingerprint, pairDifferences);
            Compare("項目数", left.ItemCount, right.ItemCount, pairDifferences);
            Compare("表示項目数", left.DisplayedItemCount, right.DisplayedItemCount, pairDifferences);
            Compare("ページ数", left.PageCount, right.PageCount, pairDifferences);
            Compare("バッジ", left.BadgeFingerprint, right.BadgeFingerprint, pairDifferences);
            Compare("本文", left.BodyFingerprint, right.BodyFingerprint, pairDifferences);
            Compare("音声種別", left.AudioCue, right.AudioCue, pairDifferences);
            differences.AddRange(pairDifferences.Select(value => $"{left.Provider} ↔ {right.Provider}: {value}"));
        }
        return new SourceComparisonResult(
            key,
            nowUtc,
            differences.Count == 0 ? SourceComparisonStatus.Equal : SourceComparisonStatus.Different,
            left.Provider,
            string.Join(", ", values.Skip(1).Select(static value => value.Provider)),
            differences.Count == 0 ? "正規化結果は一致" : $"{differences.Count}項目に差異",
            differences);
    }

    private static void Compare<T>(string name, T left, T right, List<string> output)
    {
        if (!EqualityComparer<T>.Default.Equals(left, right))
        {
            output.Add($"{name}: {left} / {right}");
        }
    }

    private static bool Equivalent(SourceComparisonResult left, SourceComparisonResult right) =>
        left.Status == right.Status &&
        string.Equals(left.ProviderA, right.ProviderA, StringComparison.Ordinal) &&
        string.Equals(left.ProviderB, right.ProviderB, StringComparison.Ordinal) &&
        string.Equals(left.Summary, right.Summary, StringComparison.Ordinal) &&
        left.Differences.SequenceEqual(right.Differences, StringComparer.Ordinal);

    private void Trim(DateTimeOffset nowUtc)
    {
        string[] expired = _observations
            .Where(pair => pair.Value.Values.All(value => nowUtc - value.ObservedAtUtc > TimeSpan.FromHours(24)))
            .Select(static pair => pair.Key).ToArray();
        foreach (string key in expired)
        {
            _observations.Remove(key);
            _results.Remove(key);
        }
        foreach (string provider in _providerLastObserved
            .Where(pair => nowUtc - pair.Value > TimeSpan.FromHours(24))
            .Select(static pair => pair.Key).ToArray())
            _providerLastObserved.Remove(provider);
    }

    private static string GetRawType(DisasterEvent value) => value switch
    {
        QuakeEvent quake => quake.Issue.RawType,
        TsunamiEvent tsunami => tsunami.Issue.RawType,
        EewEvent eew => eew.Issue.RawType,
        WeatherWarningEvent weather => weather.Issue.RawType,
        VolcanoEvent volcano => volcano.Issue.RawType,
        _ => value.Kind.ToString(),
    };

    private static string GetSerial(DisasterEvent value) => value switch
    {
        QuakeEvent quake => quake.Issue.Serial ?? string.Empty,
        TsunamiEvent tsunami => tsunami.Issue.Serial ?? string.Empty,
        EewEvent eew => eew.Issue.Serial ?? string.Empty,
        WeatherWarningEvent weather => weather.Issue.Serial ?? string.Empty,
        VolcanoEvent volcano => volcano.Issue.Serial ?? string.Empty,
        _ => string.Empty,
    };

    private static string CreateCorrelationKey(DisasterEvent value) =>
        string.Join("|", value.Id.Value, GetRawType(value), GetSerial(value));

    private sealed record SemanticObservation(
        string Provider,
        DateTimeOffset ObservedAtUtc,
        DateTimeOffset IssuedAtUtc,
        EventKind Kind,
        EventIngestionStatus Status,
        bool IsCancelled,
        bool IsCorrection,
        int ItemCount,
        int DisplayedItemCount,
        int PageCount,
        string DomainFingerprint,
        string BadgeFingerprint,
        string BodyFingerprint,
        string AudioCue)
    {
        public static SemanticObservation Create(
            string provider,
            EventIngestionResult result,
            DateTimeOffset nowUtc)
        {
            DisplayBlock[] blocks = result.Program?.Pages.SelectMany(static page => page.Blocks).ToArray() ?? [];
            return new SemanticObservation(
                provider,
                nowUtc,
                result.Event?.IssuedAt.ToUniversalTime() ?? DateTimeOffset.MinValue,
                result.Event?.Kind ?? EventKind.Quake,
                result.Status,
                result.Event?.IsCancelled == true,
                result.Event?.IsCorrection == true,
                result.NormalizedItemCount,
                result.DisplayedItemCount,
                result.Program?.Pages.Count ?? 0,
                CreateDomainFingerprint(result.Event),
                string.Join("\u001e", blocks.Select(static block => string.Join("\u001f", block.Badge, block.StyleToken))),
                string.Join("\u001e", blocks.Select(static block => string.Join("\u001f", block.PrimaryText, block.SecondaryText))),
                string.Empty);
        }

        private static string CreateDomainFingerprint(DisasterEvent? value) => value switch
        {
            QuakeEvent quake => string.Join("|", quake.Earthquake.MaximumScale, quake.Earthquake.DomesticTsunami,
                quake.Earthquake.Hypocenter?.Name, quake.Earthquake.Hypocenter?.Magnitude,
                string.Join(";", quake.Points.OrderBy(static point => point.DisplayName)
                    .Select(static point => $"{point.Prefecture}:{point.DisplayName}:{point.Scale}"))),
            EewEvent eew => string.Join("|", eew.IsWarning, eew.IsFinal, eew.IsCancelled,
                eew.Earthquake?.MaximumScale, eew.Earthquake?.Hypocenter?.Name,
                string.Join(";", eew.Areas.OrderBy(static area => area.Name)
                    .Select(static area => $"{area.Prefecture}:{area.Name}:{area.ScaleFrom}:{area.ScaleTo}"))),
            TsunamiEvent tsunami => string.Join("|", tsunami.IsCancelled,
                string.Join(";", tsunami.Areas.OrderBy(static area => area.Name)
                    .Select(static area => $"{area.Role}:{area.Name}:{area.Grade}:{area.MaximumHeight?.ValueMeters}"))),
            WeatherWarningEvent weather => string.Join("|", weather.InformationType, weather.IsCancelled,
                string.Join(";", weather.Items.OrderBy(static item => item.AreaCode).ThenBy(static item => item.KindCode)
                    .Select(static item => $"{item.AreaCode}:{item.KindCode}:{item.Level}:{item.Status}:{item.IsActive}"))),
            VolcanoEvent volcano => string.Join("|", volcano.InformationType, volcano.VolcanoCode,
                volcano.AlertLevel, volcano.AlertCondition, volcano.IsCancelled,
                string.Join(";", volcano.TargetAreas.OrderBy(static area => area.Code)
                    .Select(static area => $"{area.Code}:{area.KindCode}:{area.Status}"))),
            _ => string.Empty,
        };
    }
}

public enum SelfCheckStatus
{
    Passed = 0,
    Warning,
    Failed,
}

public sealed record SelfCheckResult(
    string Name,
    SelfCheckStatus Status,
    string Detail,
    DateTimeOffset CheckedAtUtc);

public sealed record SettingsProfileDocument(
    int SchemaVersion,
    string Name,
    DateTimeOffset CreatedAtUtc,
    string ApplicationVersion,
    AppSettings Settings)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> MigrationIssues { get; init; } = [];
}

public interface ISettingsProfileStore
{
    IReadOnlyList<string> List();
    Task SaveAsync(string name, AppSettings settings, string applicationVersion, CancellationToken cancellationToken = default);
    Task<SettingsProfileDocument> LoadAsync(string name, AppSettings currentSettings, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
    Task ExportAsync(string name, string path, CancellationToken cancellationToken = default);
    Task<SettingsProfileDocument> ImportAsync(string path, AppSettings currentSettings, CancellationToken cancellationToken = default);
}

public sealed record TestCaseExpectation(
    string EventKind,
    string Status,
    int? PageCount,
    IReadOnlyList<string> RequiredBadges,
    IReadOnlyList<string> RequiredTextFragments,
    IReadOnlyList<string> RequiredAreas,
    string AudioCue,
    string SuppressionReason)
{
    public bool? IsCancelled { get; init; }

    public bool? IsUpdate { get; init; }

    public bool? IsReleased { get; init; }
}

public sealed record TestCaseManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Category,
    string Provider,
    string TelegramType,
    string EventId,
    IReadOnlyList<string> Tags,
    string Description,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> PayloadFiles,
    string ReferenceImageFile,
    TestCaseExpectation Expectation)
{
    public const int CurrentSchemaVersion = 1;
}

public interface ITestCaseLibrary
{
    IReadOnlyList<TestCaseManifest> List();
    Task<TestCaseManifest> ImportFilesAsync(string name, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TestCaseManifest>> ImportDmdataArchiveAsync(string telegramsIndexPath, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
    Task ExportAsync(string id, string zipPath, CancellationToken cancellationToken = default);
    Task<TestCaseManifest> ImportPackageAsync(string zipPath, CancellationToken cancellationToken = default);
    Task<TestCaseManifest> DuplicateAsync(string id, CancellationToken cancellationToken = default);
    Task<TestCaseManifest> UpdateAsync(TestCaseManifest manifest, CancellationToken cancellationToken = default);
    IReadOnlyList<RawProviderMessage> LoadMessages(string id, SourceMode mode);
}
