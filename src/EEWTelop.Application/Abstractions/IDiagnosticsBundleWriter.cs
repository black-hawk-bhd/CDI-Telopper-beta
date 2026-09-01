using EEWTelop.Application.Diagnostics;

namespace EEWTelop.Application.Abstractions;

public interface IDiagnosticsBundleWriter
{
    Task WriteAsync(
        string path,
        DiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
