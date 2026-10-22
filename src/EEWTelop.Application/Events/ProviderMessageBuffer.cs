using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace EEWTelop.Application.Events;

/// <summary>
/// Bounded single-consumer FIFO for raw reception. A slow consumer waits producers instead of
/// dropping EEW updates/cancellations. Budgets include the currently yielded
/// message until its consumer advances or disposes the enumerator.
/// </summary>
public sealed class ProviderMessageBuffer
{
    public const int DefaultCapacity = 256;
    public const long DefaultMaximumBytes = 128L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly long _maximumBytes;
    private readonly Channel<BufferedMessage> _channel;
    private TaskCompletionSource _spaceAvailable = NewSignal();
    private int _count;
    private long _bytes;
    private bool _completed;
    private Exception? _completionError;

    public ProviderMessageBuffer(int capacity = DefaultCapacity, long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _capacity = capacity;
        _maximumBytes = maximumBytes;
        _channel = Channel.CreateBounded<BufferedMessage>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public async ValueTask WriteAsync(RawProviderMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        // Strings are UTF-16. Count both parsed payload and diagnostic transport;
        // the fixed allowance and item cap bound small/empty-message overhead.
        long bytes = 256 + 2L * (message.Provider.Length + (long)message.Payload.Length +
            (ReferenceEquals(message.Payload, message.TransportPayload) ? 0 : message.TransportPayload?.Length ?? 0));
        if (bytes > _maximumBytes)
            throw new InvalidDataException("受信電文が待機バッファーの容量上限を超えました。");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                if (_completed) throw new ChannelClosedException(_completionError);
                if (_count < _capacity && bytes <= _maximumBytes - _bytes)
                {
                    if (!_channel.Writer.TryWrite(new BufferedMessage(message, bytes)))
                        throw new ChannelClosedException(_completionError);
                    _count++;
                    _bytes += bytes;
                    return;
                }
                wait = _spaceAvailable.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<RawProviderMessage> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (BufferedMessage entry in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _count--;
                    _bytes -= entry.Bytes;
                    PulseWriters();
                }
            }
        }
    }

    public bool TryComplete(Exception? error = null)
    {
        lock (_gate)
        {
            if (!_channel.Writer.TryComplete(error)) return false;
            _completed = true;
            _completionError = error;
            PulseWriters();
            return true;
        }
    }

    private void PulseWriters()
    {
        TaskCompletionSource previous = _spaceAvailable;
        _spaceAvailable = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly record struct BufferedMessage(RawProviderMessage Message, long Bytes);
}
