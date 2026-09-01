using EEWTelop.Application.Operations;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public sealed class EventReceptionService
{
    private readonly IEventSource _eventSource;
    private readonly EventIngestionPipeline _pipeline;
    private readonly IRawProviderMessageArchive? _rawArchive;
    private readonly ISourceComparisonService? _comparisonService;
    private readonly IOperationalAlertCenter? _alerts;

    public EventReceptionService(
        IEventSource eventSource,
        EventIngestionPipeline pipeline,
        IRawProviderMessageArchive? rawArchive = null,
        ISourceComparisonService? comparisonService = null,
        IOperationalAlertCenter? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        ArgumentNullException.ThrowIfNull(pipeline);
        _eventSource = eventSource;
        _pipeline = pipeline;
        _rawArchive = rawArchive;
        _comparisonService = comparisonService;
        _alerts = alerts;
    }

    public ProviderConnectionSnapshot Connection => _eventSource.Connection;

    public event EventHandler<EventIngestionResult>? EventProcessed;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await foreach (RawProviderMessage raw in _eventSource
            .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await ProcessAsync(raw, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes one provider message through the same normalization,
    /// duplicate detection, display and diagnostic path as the live source.
    /// This is also the controlled entry point used by UI end-to-end tests.
    /// </summary>
    public async ValueTask<EventIngestionResult> ProcessAsync(
        RawProviderMessage raw,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);

        try
        {
            EventIngestionResult result = _pipeline.Process(raw);
            // Display first. Diagnostic disk I/O must never delay the
            // currently received disaster information from reaching OBS.
            EventProcessed?.Invoke(this, result);
            _comparisonService?.Observe(raw, result, DateTimeOffset.UtcNow);
            return result;
        }
        finally
        {
            if (_rawArchive is not null)
            {
                try
                {
                    await _rawArchive.SaveAsync(raw, cancellationToken).ConfigureAwait(false);
                    _alerts?.Recover("raw-archive-write", "生データ保存", "生データ保存が復旧しました。", DateTimeOffset.UtcNow);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _alerts?.Raise(new OperationalAlert(
                        "raw-archive-write", OperationalAlertSeverity.Error, "生データ保存失敗",
                        exception.Message, DateTimeOffset.UtcNow));
                }
            }
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
        _eventSource.StopAsync(cancellationToken);

    public void RequestReconnect(ReconnectReason reason) =>
        _eventSource.RequestReconnect(reason);

    public IReadOnlyList<ProviderBranchConnectionSnapshot> GetProviderConnections() =>
        _eventSource is IProviderConnectionDiagnostics diagnostics
            ? diagnostics.GetProviderConnections()
            : [new ProviderBranchConnectionSnapshot("現在の受信元", _eventSource.Connection)];

}
