using EEWTelop.Application.Logging;

namespace EEWTelop.Application.Abstractions;

public interface IAppLogWriter
{
    ValueTask WriteAsync(AppLogEntry entry, CancellationToken cancellationToken = default);
}

