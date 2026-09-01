using EEWTelop.Application.Abstractions;
using EEWTelop.Application.Logging;

namespace EEWTelop.Infrastructure.Logging;

public sealed class CompositeAppLogWriter : IAppLogWriter, IDisposable
{
    private readonly IReadOnlyList<IAppLogWriter> _writers;

    public CompositeAppLogWriter(params IAppLogWriter[] writers)
    {
        _writers = writers ?? throw new ArgumentNullException(nameof(writers));
    }

    public async ValueTask WriteAsync(
        AppLogEntry entry,
        CancellationToken cancellationToken = default)
    {
        foreach (IAppLogWriter writer in _writers)
        {
            try
            {
                await writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and
                not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"Log writer failed: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (IDisposable writer in _writers.OfType<IDisposable>())
        {
            writer.Dispose();
        }
    }
}
