using System.Text;
using System.Xml;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Application.History;
using EEWTelop.Application.Logging;
using EEWTelop.Domain.Events;

namespace EEWTelop.Infrastructure.Dmdata.History;

/// <summary>Reads one user-selected JMA XML telegram for an offline rehearsal.</summary>
public sealed class LocalJmaXmlHistoryMessageSource : IHistoryMessageSource
{
    public const string ProviderName = "local-jma-xml";
    public const int MaximumXmlBytes = 2 * 1024 * 1024;

    private readonly IClock _clock;
    private readonly IAppLogWriter? _logWriter;

    public LocalJmaXmlHistoryMessageSource(IClock clock, IAppLogWriter? logWriter = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _logWriter = logWriter;
    }

    public async Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
        HistoryFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Api != HistoryApi.LocalJmaXml)
        {
            throw new ArgumentException(
                "The local XML source only handles LocalJmaXml history requests.",
                nameof(request));
        }

        string configuredPath = request.LocalXmlFilePath?.Trim() ?? string.Empty;
        if (configuredPath.Length == 0)
        {
            throw new InvalidOperationException("外部XMLファイルを選択してください。");
        }

        string path = Path.GetFullPath(configuredPath);
        if (!string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("選択できる外部ファイルはXML形式のみです。");
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("選択された外部XMLファイルが見つかりません。", path);
        }
        if (file.Length is <= 0 or > MaximumXmlBytes)
        {
            throw new InvalidDataException(
                $"外部XMLファイルは1バイト以上{MaximumXmlBytes / 1024 / 1024}MiB以下にしてください。");
        }

        await LogAsync(
            AppLogLevel.Information,
            "LocalJmaXmlLoadStarted",
            $"外部JMA XMLファイルを読み込みます。file={file.Name}",
            cancellationToken).ConfigureAwait(false);

        string xml = await File.ReadAllTextAsync(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            cancellationToken).ConfigureAwait(false);
        ValidateJmaXml(xml);

        await LogAsync(
            AppLogLevel.Information,
            "LocalJmaXmlLoadCompleted",
            $"外部JMA XMLファイルを1件読み込みました。file={file.Name}",
            cancellationToken).ConfigureAwait(false);

        return
        [
            new RawProviderMessage(
                ProviderName,
                xml,
                SourceMode.HistoryRehearsal,
                _clock.UtcNow)
            {
                ContentFormat = RawProviderContentFormat.JmaXml,
            },
        ];
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void ValidateJmaXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes,
            MaxCharactersFromEntities = 0,
        };
        using var textReader = new StringReader(xml);
        using XmlReader reader = XmlReader.Create(textReader, settings);
        reader.MoveToContent();
        if (!string.Equals(reader.LocalName, "Report", StringComparison.Ordinal))
        {
            throw new InvalidDataException("気象庁防災情報XMLのReport電文ではありません。");
        }

        while (reader.Read())
        {
        }
    }

    private ValueTask LogAsync(
        AppLogLevel level,
        string eventName,
        string message,
        CancellationToken cancellationToken) => _logWriter?.WriteAsync(
            new AppLogEntry(_clock.UtcNow, level, eventName, message),
            cancellationToken) ?? ValueTask.CompletedTask;
}
