using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Configuration;

namespace EEWTelop.Infrastructure.Settings;

public sealed class InMemorySettingsStore : ISettingsStore
{
    private AppSettings _settings;

    public InMemorySettingsStore(AppSettings? initialSettings = null)
    {
        _settings = initialSettings ?? AppSettings.CreateDefault();
    }

    public ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_settings);
    }

    public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        _settings = settings;
        return ValueTask.CompletedTask;
    }
}

