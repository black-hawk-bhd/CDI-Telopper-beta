using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;
using EEWTelop.Infrastructure.P2P.Configuration;

namespace EEWTelop.Infrastructure.P2P.Recovery;

public sealed class P2pHistoryMessageSource : IHistoryMessageSource
{
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly IAppLogWriter? _logWriter;
    private readonly bool _ownsHttpClient;

    public P2pHistoryMessageSource(IClock clock, IAppLogWriter? logWriter = null)
        : this(
            new HttpClient { Timeout = TimeSpan.FromSeconds(15) },
            clock,
            ownsHttpClient: true,
            logWriter: logWriter)
    {
    }

    public P2pHistoryMessageSource(
        HttpClient httpClient,
        IClock clock,
        bool ownsHttpClient = false,
        IAppLogWriter? logWriter = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(clock);
        _httpClient = httpClient;
        _clock = clock;
        _ownsHttpClient = ownsHttpClient;
        _logWriter = logWriter;
    }

    public async Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
        HistoryFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        int limit = Math.Clamp(request.Limit, 1, 100);
        ProviderOptions provider = ProviderOptions.FromSettings(request.Provider);
        IReadOnlyList<string> validationErrors = provider.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', validationErrors));
        }

        Uri restBaseUri = request.Api == HistoryApi.JmaQuake
            ? ProviderOptions.Production.RestBaseUri
            : provider.RestBaseUri;
        string baseUrl = restBaseUri.ToString().TrimEnd('/');
        string relative = request.Api switch
        {
            HistoryApi.JmaQuake => FormattableString.Invariant(
                $"jma/quake?limit={limit}&order=-1"),
            HistoryApi.History => FormattableString.Invariant(
                $"history?codes=551&codes=552&codes=556&limit={limit}"),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Api, "Unknown history API."),
        };
        var requestUri = new Uri($"{baseUrl}/{relative}", UriKind.Absolute);
        await LogAsync(
            AppLogLevel.Information,
            "HistoryFetchStarted",
            $"履歴APIへ取得を開始しました。api={request.Api} limit={limit}",
            cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"履歴APIがHTTP {(int)response.StatusCode} ({response.ReasonPhrase})を返しました。api={request.Api}",
                    inner: null,
                    response.StatusCode);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The history API response root must be an array.");
            }

            DateTimeOffset receivedAt = _clock.UtcNow;
            RawProviderMessage[] messages = document.RootElement
                .EnumerateArray()
                .Select(element => new RawProviderMessage(
                    "p2pquake-history",
                    element.GetRawText(),
                    SourceMode.HistoryRehearsal,
                    receivedAt))
                .ToArray();
            await LogAsync(
                AppLogLevel.Information,
                "HistoryFetchCompleted",
                $"履歴APIから{messages.Length}件を取得しました。api={request.Api}",
                cancellationToken).ConfigureAwait(false);
            return messages;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            await LogAsync(
                AppLogLevel.Error,
                "HistoryFetchFailed",
                $"履歴APIの取得に失敗しました。api={request.Api}: {exception.Message}",
                CancellationToken.None,
                exception).ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        CancellationToken cancellationToken,
        Exception? exception = null) => _logWriter?.WriteAsync(
            new AppLogEntry(_clock.UtcNow, level, eventName, message, exception),
            cancellationToken) ?? ValueTask.CompletedTask;
}
