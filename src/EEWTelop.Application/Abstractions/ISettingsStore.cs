using EEWTelop.Application.Configuration;

namespace EEWTelop.Application.Abstractions;

public interface ISettingsStore
{
    ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

