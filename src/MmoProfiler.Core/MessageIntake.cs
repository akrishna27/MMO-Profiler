using System.Threading.Channels;

namespace MmoProfiler.Core;

/// <summary>
/// Bounded, lock-free hand-off from the game thread to the engine. <see cref="Accept"/> is O(1),
/// never blocks, never throws and performs no I/O.
/// </summary>
public sealed class MessageIntake
{
    private readonly Func<ProfilerOptions> _options;
    private readonly Channel<ChatMessage> _channel;
    private long _dropped;
    private CharacterKey? _selfKey;

    /// <remarks>The queue capacity is read once, here; changing it later needs a new instance.</remarks>
    public MessageIntake(Func<ProfilerOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var capacity = Math.Max(1, options().QueueCapacity);
        _channel = Channel.CreateBounded<ChatMessage>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public int Count => _channel.Reader.Count;

    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Messages from this key are ignored.</summary>
    public CharacterKey? SelfKey
    {
        get => Volatile.Read(ref _selfKey);
        set => Volatile.Write(ref _selfKey, value);
    }

    /// <summary>Called on the game thread. Returns true if the message was queued.</summary>
    public bool Accept(ChatMessage message)
    {
        try
        {
            var o = _options();
            if (!o.Enabled || !o.EnabledChannels.Contains(message.Channel)) return false;

            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var self = SelfKey;
            if (self is not null && self.Equals(message.Sender)) return false;

            var max = Math.Max(1, o.MaxMessageLength);
            if (text.Length > max) message = message with { Text = text[..max] };

            return _channel.Writer.TryWrite(message);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Moves up to <paramref name="max"/> messages, oldest first, into <paramref name="into"/>.</summary>
    public int Drain(List<ChatMessage> into, int max)
    {
        ArgumentNullException.ThrowIfNull(into);
        var n = 0;
        while (n < max && _channel.Reader.TryRead(out var m))
        {
            into.Add(m);
            n++;
        }
        return n;
    }
}
