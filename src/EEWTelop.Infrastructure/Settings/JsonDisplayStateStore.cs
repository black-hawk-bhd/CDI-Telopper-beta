using System.Text.Json;
using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Logging;
using EEWTelop.Application.Persistence;
using EEWTelop.Infrastructure.Persistence;

namespace EEWTelop.Infrastructure.Settings;

public sealed class JsonDisplayStateStore : IDisplayStateStore
{
    private readonly string _path;
    private readonly IAppLogWriter _log;
    private readonly JsonSerializerOptions _json = JsonFileOptions.Create();

    public JsonDisplayStateStore(string path, IAppLogWriter log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        _path = Path.GetFullPath(path);
        _log = log;
    }

    public async ValueTask<DisplayStateDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return DisplayStateDocument.Empty(DateTimeOffset.UtcNow);
        }

        try
        {
            await using FileStream stream = File.OpenRead(_path);
            DisplayStateDocument? state =
                await JsonSerializer.DeserializeAsync<DisplayStateDocument>(
                    stream,
                    _json,
                    cancellationToken).ConfigureAwait(false);
            if (state is null || state.SchemaVersion != DisplayStateDocument.CurrentSchemaVersion ||
                state.Pending is null || state.RecentSignatures is null)
            {
                throw new InvalidDataException("The state document failed schema validation.");
            }

            ValidateIntegrity(state.Current);
            ValidateIntegrity(state.PersistentTsunami);
            foreach (StoredDisplayProgram item in state.Pending)
            {
                ValidateIntegrity(item);
            }

            if (state.RecentSignatures.Any(static item =>
                string.IsNullOrWhiteSpace(item.Provider) ||
                string.IsNullOrWhiteSpace(item.EventId) ||
                string.IsNullOrWhiteSpace(item.Signature) ||
                !Enum.IsDefined(item.Kind)))
            {
                throw new InvalidDataException("The state signature cache failed integrity validation.");
            }

            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            string backup = AtomicFileWriter.MoveAsideCorruptFile(_path, DateTimeOffset.Now);
            await _log.WriteAsync(new AppLogEntry(
                DateTimeOffset.UtcNow,
                AppLogLevel.Warning,
                "StateRecovered",
                $"状態JSONが不正なため空の状態で起動しました。退避先: {Path.GetFileName(backup)}",
                exception), cancellationToken).ConfigureAwait(false);
            return DisplayStateDocument.Empty(DateTimeOffset.UtcNow);
        }
    }

    public ValueTask SaveAsync(
        DisplayStateDocument state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != DisplayStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Cannot save an unsupported state schema.");
        }

        return new ValueTask(AtomicFileWriter.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, state, _json, token),
            cancellationToken));
    }

    private static void ValidateIntegrity(StoredDisplayProgram? item)
    {
        if (item is not null && !item.TryToProgram(out _))
        {
            throw new InvalidDataException("A persisted display program failed integrity validation.");
        }
    }
}
