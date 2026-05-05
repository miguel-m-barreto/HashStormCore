using System.Threading.Channels;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Abstractions;
using Newtonsoft.Json;

namespace HashStormCore.Eventing.Queue;

public class InMemoryShareEventQueue : IShareEventQueue
{
    public InMemoryShareEventQueue(ShareEventHandoffOptions options = null)
    {
        this.options = options ?? new ShareEventHandoffOptions();
        channel = Channel.CreateUnbounded<QueuedShareEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    private readonly ShareEventHandoffOptions options;
    private readonly Channel<QueuedShareEvent> channel;
    private int count;
    private long approximateBytes;

    public int Count => Volatile.Read(ref count);
    public long ApproximateBytes => Volatile.Read(ref approximateBytes);
    public bool IsAboveSoftThreshold => Count >= options.SoftMaxBufferedEvents || ApproximateBytes >= options.SoftMaxBufferedBytes;
    public bool IsAboveCriticalThreshold => Count >= options.CriticalBufferedEvents || ApproximateBytes >= options.CriticalBufferedBytes;

    public async ValueTask EnqueueAsync(ShareEvent shareEvent, CancellationToken ct)
    {
        var bytes = EstimateSize(shareEvent);
        var queued = new QueuedShareEvent(shareEvent, bytes);

        await channel.Writer.WriteAsync(queued, ct);
        AddQueued(bytes);
    }

    private void AddQueued(int bytes)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(ref approximateBytes, bytes);
    }

    public async ValueTask<ShareEvent> DequeueAsync(CancellationToken ct)
    {
        var queued = await channel.Reader.ReadAsync(ct);
        Interlocked.Decrement(ref count);
        Interlocked.Add(ref approximateBytes, -queued.ApproximateBytes);
        return queued.Event;
    }

    public bool TryDequeue(out ShareEvent shareEvent)
    {
        if(channel.Reader.TryRead(out var queued))
        {
            Interlocked.Decrement(ref count);
            Interlocked.Add(ref approximateBytes, -queued.ApproximateBytes);
            shareEvent = queued.Event;
            return true;
        }

        shareEvent = null;
        return false;
    }

    private static int EstimateSize(ShareEvent shareEvent)
    {
        return Math.Max(128, JsonConvert.SerializeObject(shareEvent).Length);
    }

    private readonly record struct QueuedShareEvent(ShareEvent Event, int ApproximateBytes);
}

public class ShareEventHandoffOptions
{
    public int SoftMaxBufferedEvents { get; set; } = 100_000;
    public long SoftMaxBufferedBytes { get; set; } = 268_435_456;
    public int CriticalBufferedEvents { get; set; } = 500_000;
    public long CriticalBufferedBytes { get; set; } = 1_073_741_824;
}
