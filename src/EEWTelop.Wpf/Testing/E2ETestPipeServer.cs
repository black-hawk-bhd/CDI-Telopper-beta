using System.IO.Pipes;
using System.Text;

namespace EEWTelop.Wpf.Testing;

/// <summary>
/// Named-pipe receiver enabled only for an explicitly launched UI E2E test.
/// It is never started during normal operation.
/// </summary>
internal sealed class E2ETestPipeServer : IAsyncDisposable
{
    private const string EnabledEnvironmentVariable = "QT_E2E";
    private const string PipeEnvironmentVariable = "QT_E2E_PIPE";
    private readonly Func<string, CancellationToken, ValueTask> _onJson;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loopTask;

    private E2ETestPipeServer(
        string pipeName,
        Func<string, CancellationToken, ValueTask> onJson)
    {
        _onJson = onJson;
        _loopTask = RunAsync(pipeName, _stop.Token);
    }

    public static E2ETestPipeServer? StartIfEnabled(
        Func<string, CancellationToken, ValueTask> onJson)
    {
        ArgumentNullException.ThrowIfNull(onJson);

        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnabledEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        string? pipeName = Environment.GetEnvironmentVariable(PipeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(pipeName)
            ? null
            : new E2ETestPipeServer(pipeName, onJson);
    }

    private async Task RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: true);
                string? json = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    await _onJson(json, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
