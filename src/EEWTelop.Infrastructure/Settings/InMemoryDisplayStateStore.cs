using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Persistence;

namespace EEWTelop.Infrastructure.Settings;

public sealed class InMemoryDisplayStateStore : IDisplayStateStore
{
    private DisplayStateDocument _state;

    public InMemoryDisplayStateStore(DisplayStateDocument? state = null)
    {
        _state = state ?? DisplayStateDocument.Empty(DateTimeOffset.UtcNow);
    }

    public ValueTask<DisplayStateDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_state);
    }

    public ValueTask SaveAsync(
        DisplayStateDocument state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        _state = state;
        return ValueTask.CompletedTask;
    }
}
