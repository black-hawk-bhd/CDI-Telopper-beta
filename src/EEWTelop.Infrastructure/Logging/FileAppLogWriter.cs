using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Logging;

namespace EEWTelop.Infrastructure.Logging;

public sealed class FileAppLogWriter : IAppLogWriter, IDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAppLogWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async ValueTask WriteAsync(
        AppLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(
                _directory,
                $"CDI-Telopper-{entry.Timestamp.UtcDateTime:yyyyMMdd}.log");
            string line = JsonSerializer.Serialize(new
            {
                timestamp = entry.Timestamp,
                level = entry.Level.ToString(),
                eventName = LogTextSanitizer.Sanitize(entry.EventName),
                message = LogTextSanitizer.Sanitize(entry.Message),
                exceptionType = entry.Exception?.GetType().FullName,
                exceptionMessage = LogTextSanitizer.Sanitize(entry.Exception?.Message),
                exceptionDetails = LogTextSanitizer.Sanitize(entry.Exception?.ToString()),
            });
            await File.AppendAllTextAsync(
                path,
                line + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
