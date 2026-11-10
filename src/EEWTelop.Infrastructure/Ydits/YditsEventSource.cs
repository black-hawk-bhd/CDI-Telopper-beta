using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Ydits;

public sealed class YditsEventSource : IEventSource, IProviderConfigurableEventSource
{
    private static readonly Uri[] ProductionEndpoints =
    [
        new("https://api.ydits.net/vxse43"),
        new("https://api2.ydits.net/vxse43"),
        new("https://api3.ydits.net/vxse43"),
    ];
    private static readonly Uri[] TestEndpoints = [new("https://api.ydits.net/test/vxse43")];
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);
    private const int MaximumResponseBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly HttpClient _http;
    private CancellationTokenSource? _reader;
    private long? _lastAttemptFinished;
    private TimeSpan _cooldown = PollInterval;
    private YditsEventNormalizer.WarningReport? _latestReport;
    private YditsEventNormalizer.WarningReport? _latestTestReport;
    private bool _testMode;
    private bool _disposed;

    public YditsEventSource(IClock clock)
        : this(clock, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })) { }

    public YditsEventSource(IClock clock, HttpClient http)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        Connection = new(ProviderConnectionState.Stopped, _clock.UtcNow);
    }

    public ProviderConnectionSnapshot Connection { get; private set; }
    public event EventHandler<ProviderConnectionSnapshot>? ConnectionChanged;

    public void ConfigureProvider(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reader is not null) throw new InvalidOperationException("Stop reception before configuring YDITS.");
            _testMode = settings.Routing.Eew == ReceptionProvider.YditsTest;
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationTokenSource reader;
        bool isTest;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reader is not null) throw new InvalidOperationException("Only one YDITS reader is allowed.");
            reader = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _reader = reader;
            isTest = _testMode;
        }

        CancellationToken token = reader.Token;
        Uri[] endpoints = isTest ? TestEndpoints : ProductionEndpoints;
        string feedLabel = isTest ? "YDITS VXSE43 テスト電文" : "YDITS VXSE43";
        int endpoint = 0;
        bool healthyMirror = false;
        long lastPrimaryAttempt = _clock.GetTimestamp();
        try
        {
            SetState(ProviderConnectionState.Connecting, feedLabel);
            while (!token.IsCancellationRequested)
            {
                if (_lastAttemptFinished is long last)
                {
                    TimeSpan wait = _cooldown - _clock.GetElapsedTime(last);
                    while (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 1, 30_000)),
                            token).ConfigureAwait(false);
                        wait = _cooldown - _clock.GetElapsedTime(last);
                    }
                }

                // A healthy mirror is polled on its own until a spaced primary recovery check.
                if (healthyMirror && _clock.GetElapsedTime(lastPrimaryAttempt) >= RecoveryInterval)
                    endpoint = 0;
                if (endpoint == 0) lastPrimaryAttempt = _clock.GetTimestamp();

                RawProviderMessage? message = null;
                _cooldown = PollInterval;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await _http.GetAsync(endpoints[endpoint],
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date - _clock.UtcNow);
                        TimeSpan minimum = response.StatusCode == HttpStatusCode.TooManyRequests
                            ? FailureCooldown : PollInterval;
                        _cooldown = retryAfter > minimum ? retryAfter.Value : minimum;
                    }
                    response.EnsureSuccessStatusCode();
                    await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
                    string payload;
                    try { payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false); }
                    catch (InvalidOperationException ex) { throw new InvalidDataException("Invalid YDITS response encoding.", ex); }
                    DateTimeOffset receivedAt = _clock.UtcNow;
                    var (isActive, report) = YditsEventNormalizer.ReadResponse(payload, receivedAt, isTest);
                    healthyMirror = endpoint != 0;
                    var previous = isTest ? _latestTestReport : _latestReport;
                    if (report is not null && ShouldPublish(isTest || isActive, report, previous))
                    {
                        if (isTest) _latestTestReport = report;
                        else _latestReport = report;
                        message = new RawProviderMessage(
                            isTest ? YditsEventNormalizer.TestProviderName : YditsEventNormalizer.ProviderName,
                            payload, isTest ? SourceMode.Sandbox : SourceMode.Production, receivedAt);
                    }
                    SetState(ProviderConnectionState.Connected, isTest ? feedLabel : $"{feedLabel} サーバー {endpoint + 1}",
                        message?.ReceivedAt);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { yield break; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                    OperationCanceledException)
                {
                    int failed = endpoint;
                    healthyMirror = false;
                    endpoint = (endpoint + 1) % endpoints.Length;
                    if (endpoint == 0 && _cooldown < FailureCooldown) _cooldown = FailureCooldown;
                    SetState(ProviderConnectionState.Reconnecting,
                        isTest ? $"{feedLabel} 取得失敗" : $"{feedLabel} サーバー {failed + 1} 取得失敗",
                        retryDelay: _cooldown);
                }
                finally { _lastAttemptFinished = _clock.GetTimestamp(); }

                if (message is not null) yield return message;
            }
        }
        finally
        {
            try { SetState(ProviderConnectionState.Stopped, "停止"); }
            finally
            {
                lock (_gate)
                {
                    _reader = null;
                    reader.Dispose();
                }
            }
        }
    }

    private static bool ShouldPublish(bool canStart, YditsEventNormalizer.WarningReport report,
        YditsEventNormalizer.WarningReport? previous)
    {
        // Inactive snapshots and cancellations may only update a warning already
        // seen on this feed. Repeated snapshots do not enter the reception archive.
        if ((!canStart || report.IsCancelled) && previous?.Id != report.Id) return false;
        if (previous is null) return true;
        if (report.IssuedAt < previous.IssuedAt) return false;
        if (report.Id != previous.Id) return true;
        if (previous.IsCancelled || (previous.IsFinal && !report.IsCancelled)) return false;
        return report.IssuedAt > previous.IssuedAt || report.IsFinal;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _reader?.Cancel();
        return ValueTask.CompletedTask;
    }

    // Polling resumes on its normal cadence, even after network or system-resume events.
    public void RequestReconnect(ReconnectReason reason) { }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _reader?.Cancel();
            _http.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void SetState(ProviderConnectionState state, string detail, DateTimeOffset? receivedAt = null,
        TimeSpan? retryDelay = null)
    {
        var previous = Connection;
        receivedAt ??= previous.LastReceivedAt;
        if (previous.State == state && previous.Detail == detail &&
            previous.LastReceivedAt == receivedAt && previous.RetryDelay == retryDelay) return;
        var next = new ProviderConnectionSnapshot(state, _clock.UtcNow, receivedAt, retryDelay, detail);
        Connection = next;
        ConnectionChanged?.Invoke(this, next);
    }
}
