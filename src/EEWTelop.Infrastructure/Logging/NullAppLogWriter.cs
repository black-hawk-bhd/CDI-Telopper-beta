using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Logging;

namespace EEWTelop.Infrastructure.Logging;

public sealed class NullAppLogWriter : IAppLogWriter
{
    public ValueTask WriteAsync(AppLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

