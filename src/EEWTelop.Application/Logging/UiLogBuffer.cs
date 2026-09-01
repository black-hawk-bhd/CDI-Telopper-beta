using EEWTelop.Application.Abstractions;

namespace EEWTelop.Application.Logging;

public sealed class UiLogBuffer : IAppLogWriter
{
    public const int MaximumCapacity = 250;

    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _entries = new(MaximumCapacity);

    public event EventHandler<AppLogEntry>? EntryAdded;

    public ValueTask WriteAsync(
        AppLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaximumCapacity)
            {
                _entries.Dequeue();
            }
        }

        EntryAdded?.Invoke(this, entry);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<AppLogEntry> GetSnapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
