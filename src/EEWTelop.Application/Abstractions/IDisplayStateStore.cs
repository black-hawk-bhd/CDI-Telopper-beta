using EEWTelop.Application.Persistence;

namespace EEWTelop.Application.Abstractions;

public interface IDisplayStateStore
{
    ValueTask<DisplayStateDocument> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        DisplayStateDocument state,
        CancellationToken cancellationToken = default);
}
