using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace EEWTelop.Infrastructure.Dmdata.Transport;

internal enum DmdataFrameKind
{
    Start = 0,
    Ping,
    Pong,
    Data,
    Error,
    Unknown,
}

internal sealed record DmdataDecodedFrame(
    DmdataFrameKind Kind,
    string? PingId = null,
    string? Xml = null,
    string? TelegramType = null,
    bool IsTest = false,
    string? Error = null,
    bool CloseRequested = false);

internal static class DmdataWebSocketFrameDecoder
{
    private const int MaximumExpandedXmlBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static DmdataDecodedFrame Decode(string frameJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameJson);
        using JsonDocument document = JsonDocument.Parse(frameJson, new JsonDocumentOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        JsonElement root = document.RootElement;
        string? type = ReadString(root, "type");
        return type switch
        {
            "start" => new DmdataDecodedFrame(DmdataFrameKind.Start),
            "ping" => new DmdataDecodedFrame(
                DmdataFrameKind.Ping,
                PingId: ReadString(root, "pingId")),
            "pong" => new DmdataDecodedFrame(DmdataFrameKind.Pong),
            "data" => DecodeData(root),
            "error" => new DmdataDecodedFrame(
                DmdataFrameKind.Error,
                Error: ReadString(root, "error") ?? "Unknown WebSocket error",
                CloseRequested: ReadBoolean(root, "close")),
            _ => new DmdataDecodedFrame(DmdataFrameKind.Unknown),
        };
    }

    public static string CreatePong(string? pingId) => string.IsNullOrEmpty(pingId)
        ? "{\"type\":\"pong\"}"
        : JsonSerializer.Serialize(new { type = "pong", pingId });

    private static DmdataDecodedFrame DecodeData(JsonElement root)
    {
        string? format = ReadString(root, "format");
        if (!string.Equals(format, "xml", StringComparison.Ordinal))
        {
            return new DmdataDecodedFrame(
                DmdataFrameKind.Unknown,
                Error: $"Unsupported DMDATA.JP body format '{format ?? "null"}'.");
        }

        string body = ReadString(root, "body") ??
            throw new InvalidDataException("DMDATA.JP data frame did not contain a body.");
        string? encoding = ReadString(root, "encoding");
        string? compression = ReadString(root, "compression");
        byte[] encoded = encoding switch
        {
            "base64" => Convert.FromBase64String(body),
            "utf-8" or null => StrictUtf8.GetBytes(body),
            _ => throw new InvalidDataException(
                $"Unsupported DMDATA.JP body encoding '{encoding}'."),
        };
        byte[] xmlBytes = compression switch
        {
            "gzip" => ExpandGzip(encoded),
            "zip" => ExpandZip(encoded),
            null => encoded,
            _ => throw new InvalidDataException(
                $"Unsupported DMDATA.JP compression '{compression}'."),
        };
        if (xmlBytes.Length > MaximumExpandedXmlBytes)
        {
            throw new InvalidDataException("Expanded DMDATA.JP XML exceeded the safety limit.");
        }

        string xml = StrictUtf8.GetString(xmlBytes);
        ValidateXml(xml);
        JsonElement head = root.TryGetProperty("head", out JsonElement value)
            ? value
            : default;
        return new DmdataDecodedFrame(
            DmdataFrameKind.Data,
            Xml: xml,
            TelegramType: head.ValueKind == JsonValueKind.Object
                ? ReadString(head, "type")
                : null,
            IsTest: head.ValueKind == JsonValueKind.Object && ReadBoolean(head, "test"));
    }

    private static byte[] ExpandGzip(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumExpandedXmlBytes)
            {
                throw new InvalidDataException("Expanded DMDATA.JP XML exceeded the safety limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static byte[] ExpandZip(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry[] files = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        if (files.Length != 1)
        {
            throw new InvalidDataException(
                "DMDATA.JP ZIP body must contain exactly one file.");
        }

        ZipArchiveEntry entry = files[0];
        if (entry.Length > MaximumExpandedXmlBytes)
        {
            throw new InvalidDataException("Expanded DMDATA.JP XML exceeded the safety limit.");
        }

        using Stream zipEntry = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = zipEntry.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumExpandedXmlBytes)
            {
                throw new InvalidDataException("Expanded DMDATA.JP XML exceeded the safety limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static void ValidateXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumExpandedXmlBytes,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read())
        {
        }
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;
}
