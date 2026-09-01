using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Diagnostics;
using EEWTelop.Infrastructure.Logging;
using EEWTelop.Infrastructure.Persistence;

namespace EEWTelop.Infrastructure.Diagnostics;

public sealed class ZipDiagnosticsBundleWriter : IDiagnosticsBundleWriter
{
    private static readonly string[] SecretNames =
        ["apikey", "api_key", "token", "password", "secret", "credential"];
    private readonly JsonSerializerOptions _json = JsonFileOptions.Create();

    public Task WriteAsync(
        string path,
        DiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        return AtomicFileWriter.WriteAsync(
            path,
            (stream, token) => WriteArchiveAsync(stream, snapshot, token),
            cancellationToken);
    }

    private async Task WriteArchiveAsync(
        Stream output,
        DiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonAsync(archive, "summary.json", new
        {
            snapshot.SchemaVersion,
            snapshot.CreatedAtUtc,
            snapshot.ApplicationVersion,
            snapshot.RuntimeVersion,
            snapshot.OperatingSystem,
            snapshot.ConnectionState,
            snapshot.LastReceivedAtUtc,
            snapshot.ReconnectCount,
            ObsStatus = LogTextSanitizer.Sanitize(snapshot.ObsStatus),
            snapshot.ObsClientCount,
            snapshot.LastObsAudioCue,
            snapshot.LastObsAudioPlaybackResult,
            snapshot.LastObsAudioPlaybackAtUtc,
        }, cancellationToken).ConfigureAwait(false);

        JsonNode settings = JsonSerializer.SerializeToNode(snapshot.Settings, _json)
            ?? new JsonObject();
        Redact(settings, propertyName: string.Empty);
        await WriteTextAsync(
            archive,
            "settings.redacted.json",
            settings.ToJsonString(_json),
            cancellationToken).ConfigureAwait(false);

        string logs = string.Join(Environment.NewLine, snapshot.Logs.Select(entry =>
            JsonSerializer.Serialize(new
            {
                entry.Timestamp,
                level = entry.Level.ToString(),
                eventName = LogTextSanitizer.Sanitize(entry.EventName),
                message = LogTextSanitizer.Sanitize(entry.Message),
                exceptionType = entry.Exception?.GetType().FullName,
                exceptionMessage = LogTextSanitizer.Sanitize(entry.Exception?.Message),
            })));
        await WriteTextAsync(archive, "logs.jsonl", logs, cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(archive, "operations/alerts.json", snapshot.OperationalAlerts, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(archive, "operations/source-comparisons.json", snapshot.SourceComparisons, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(archive, "operations/provider-connections.json", snapshot.ProviderConnections, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(archive, "operations/obs-routes.json", snapshot.ObsRouteConnections, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteJsonAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken) => await WriteTextAsync(
            archive, name, SerializeRedacted(value), cancellationToken).ConfigureAwait(false);

    private string SerializeRedacted<T>(T value)
    {
        JsonNode node = JsonSerializer.SerializeToNode(value, _json) ?? new JsonObject();
        Redact(node, propertyName: string.Empty);
        return node.ToJsonString(_json);
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream stream = entry.Open();
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true);
        await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Redact(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
            {
                if (IsSecret(property.Key))
                {
                    obj[property.Key] = "***";
                }
                else
                {
                    Redact(property.Value, property.Key);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                Redact(item, propertyName);
            }
        }
        else if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            value.ReplaceWith(LogTextSanitizer.Sanitize(text));
        }
    }

    private static bool IsSecret(string name) => SecretNames.Any(secret =>
        name.Contains(secret, StringComparison.OrdinalIgnoreCase));
}
