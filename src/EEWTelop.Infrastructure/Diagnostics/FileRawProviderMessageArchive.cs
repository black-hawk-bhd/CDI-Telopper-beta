using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.Logging;

namespace EEWTelop.Infrastructure.Diagnostics;

public sealed class FileRawProviderMessageArchive : IRawProviderMessageArchive, IDisposable
{
    private const int MaximumSinglePayloadBytes = 24 * 1024 * 1024;
    private readonly string _directory;
    private readonly IAppLogWriter _logWriter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LogSettings _settings;
    private bool _disposed;

    public FileRawProviderMessageArchive(
        string directory,
        LogSettings settings,
        IAppLogWriter logWriter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logWriter);
        _directory = Path.GetFullPath(directory);
        _settings = Normalize(settings);
        _logWriter = logWriter;
    }

    public void Configure(LogSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, Normalize(settings));
    }

    public async ValueTask SaveAsync(
        RawProviderMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);
        LogSettings settings = Volatile.Read(ref _settings);
        if (!settings.SaveRawProviderMessages)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            string stem = BuildStem(message);
            await WritePayloadAsync(
                message,
                message.Payload,
                message.ContentFormat,
                stem,
                "provider",
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(message.TransportPayload) &&
                !string.Equals(
                    message.TransportPayload,
                    message.Payload,
                    StringComparison.Ordinal))
            {
                await WritePayloadAsync(
                    message,
                    message.TransportPayload,
                    message.TransportContentFormat ?? RawProviderContentFormat.Json,
                    stem,
                    "transport",
                    cancellationToken).ConfigureAwait(false);
            }

            Cleanup(settings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and
            not StackOverflowException and not OutOfMemoryException)
        {
            await _logWriter.WriteAsync(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppLogLevel.Warning,
                "RawProviderMessageArchiveFailed",
                "受信生データを保存できませんでした。",
                exception), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WritePayloadAsync(
        RawProviderMessage message,
        string payload,
        RawProviderContentFormat format,
        string stem,
        string suffix,
        CancellationToken cancellationToken)
    {
        int byteCount = Encoding.UTF8.GetByteCount(payload);
        if (byteCount > MaximumSinglePayloadBytes)
        {
            await _logWriter.WriteAsync(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppLogLevel.Warning,
                "RawProviderMessageTooLarge",
                $"受信生データが1件上限24MBを超えたため保存しませんでした。provider={Sanitize(message.Provider)} bytes={byteCount.ToString(CultureInfo.InvariantCulture)}"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string extension = format == RawProviderContentFormat.JmaXml ? "xml" : "json";
        string finalPath = Path.Combine(_directory, $"{stem}.{suffix}.{extension}");
        string temporaryPath = finalPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                payload,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildStem(RawProviderMessage message)
    {
        ArchiveIdentity providerIdentity = ReadIdentity(
            message.Payload,
            message.ContentFormat);
        ArchiveIdentity transportIdentity = string.IsNullOrWhiteSpace(message.TransportPayload)
            ? default
            : ReadIdentity(
                message.TransportPayload,
                message.TransportContentFormat ?? RawProviderContentFormat.Json);
        ArchiveIdentity identity = providerIdentity.Merge(transportIdentity);

        var parts = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"{message.ReceivedAt.UtcDateTime:yyyyMMddTHHmmss.fffffffZ}"),
            Sanitize(message.Provider),
            Sanitize(message.SourceMode.ToString()),
        };
        AddIdentityPart(parts, identity.Channel);
        AddIdentityPart(parts, identity.TelegramType);
        AddIdentityPart(parts, identity.EventId);

        // Keep a short collision guard, but place readable identifiers before it.
        // The same stem is shared by provider.xml and transport.json.
        parts.Add(Guid.NewGuid().ToString("N")[..8]);
        return string.Join('_', parts);
    }

    private static void AddIdentityPart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(Sanitize(value));
        }
    }

    private static ArchiveIdentity ReadIdentity(
        string payload,
        RawProviderContentFormat format)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return default;
        }

        try
        {
            return format == RawProviderContentFormat.JmaXml
                ? ReadXmlIdentity(payload)
                : ReadJsonIdentity(payload);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
            InvalidOperationException or System.Xml.XmlException)
        {
            // Archiving must still succeed even when the received body is malformed.
            return default;
        }
    }

    private static ArchiveIdentity ReadXmlIdentity(string payload)
    {
        XDocument document = XDocument.Parse(payload, LoadOptions.None);
        string identifier = document.Root?.Attributes()
            .FirstOrDefault(static attribute =>
                attribute.Name.LocalName.Equals("uuid", StringComparison.OrdinalIgnoreCase) ||
                attribute.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;
        string telegramType = document.Descendants()
            .Where(static element =>
                element.Name.LocalName.Equals("Type", StringComparison.OrdinalIgnoreCase) &&
                element.Parent?.Name.LocalName.Equals(
                    "Control",
                    StringComparison.OrdinalIgnoreCase) == true)
            .Select(static element => element.Value.Trim())
            .FirstOrDefault(IsTelegramTypeToken) ??
            ExtractTelegramType(identifier);
        string eventId = document.Descendants()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals("EventID", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? string.Empty;
        return new ArchiveIdentity(string.Empty, telegramType, eventId);
    }

    private static ArchiveIdentity ReadJsonIdentity(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        string channel = ReadJsonString(root, "channel");
        JsonElement message = TryGetProperty(root, "message", out JsonElement messageElement)
            ? messageElement
            : root;
        string identifier = FirstNonBlank(
            ReadJsonString(message, "uuid_"),
            ReadJsonString(message, "uuid"),
            ReadJsonString(message, "id"));
        string telegramType = FirstNonBlank(
            ReadJsonString(root, "telegramType"),
            ReadJsonString(message, "telegramType"),
            ExtractTelegramType(identifier));
        string eventId = string.Empty;
        if (TryGetProperty(message, "Head", out JsonElement head))
        {
            eventId = ReadJsonString(head, "EventID");
        }

        return new ArchiveIdentity(channel, telegramType, eventId);
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ReadJsonString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static string ExtractTelegramType(string identifier) => identifier
        .Split(
            ['_', '-', '/', ':'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(IsTelegramTypeToken) ?? string.Empty;

    private static bool IsTelegramTypeToken(string value) =>
        value.Length == 6 &&
        value[..4].All(static character => character is >= 'A' and <= 'Z') &&
        value[4..].All(char.IsDigit);

    private static string FirstNonBlank(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private readonly record struct ArchiveIdentity(
        string? Channel,
        string? TelegramType,
        string? EventId)
    {
        public ArchiveIdentity Merge(ArchiveIdentity fallback) => new(
            FirstNonBlank(Channel ?? string.Empty, fallback.Channel ?? string.Empty),
            FirstNonBlank(TelegramType ?? string.Empty, fallback.TelegramType ?? string.Empty),
            FirstNonBlank(EventId ?? string.Empty, fallback.EventId ?? string.Empty));
    }

    private void Cleanup(LogSettings settings)
    {
        var files = new DirectoryInfo(_directory)
            .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(static file => !file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file.LastWriteTimeUtc)
            .ToList();
        DateTime cutoff = DateTime.UtcNow.AddDays(-settings.RawMessageRetentionDays);
        foreach (FileInfo expired in files.Where(file => file.LastWriteTimeUtc < cutoff).ToArray())
        {
            expired.Delete();
            files.Remove(expired);
        }

        long maximumBytes = settings.RawMessageMaximumTotalMegabytes * 1024L * 1024L;
        long totalBytes = files.Sum(static file => file.Length);
        foreach (FileInfo oldest in files)
        {
            if (totalBytes <= maximumBytes)
            {
                break;
            }

            long length = oldest.Length;
            oldest.Delete();
            totalBytes -= length;
        }
    }

    private static LogSettings Normalize(LogSettings settings) => settings with
    {
        RawMessageRetentionDays = Math.Clamp(settings.RawMessageRetentionDays, 1, 90),
        RawMessageMaximumTotalMegabytes = Math.Clamp(
            settings.RawMessageMaximumTotalMegabytes,
            32,
            4096),
    };

    private static string Sanitize(string value)
    {
        string cleaned = string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned[..Math.Min(cleaned.Length, 48)];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
