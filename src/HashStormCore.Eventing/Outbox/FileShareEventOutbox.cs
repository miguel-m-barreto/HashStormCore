using System.Buffers.Binary;
using HashStormCore.Contracts.Eventing;
using Newtonsoft.Json;

namespace HashStormCore.Eventing.Outbox;

public class FileShareEventOutbox : IShareEventOutbox, IAsyncDisposable
{
    public const uint Magic = 0x48534556;
    public const ushort Version = 1;

    public FileShareEventOutbox(ShareEventOutboxOptions options)
    {
        this.options = options;
        Directory.CreateDirectory(options.Directory);
        OpenLastSegment();
    }

    private readonly ShareEventOutboxOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream currentStream;
    private string currentSegmentName = string.Empty;
    private long currentSegmentIndex;

    public async Task AppendAsync(IReadOnlyList<ShareEvent> events, bool flushToDisk, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach(var shareEvent in events)
            {
                ct.ThrowIfCancellationRequested();

                if(string.IsNullOrWhiteSpace(shareEvent.EventId))
                    throw new InvalidOperationException("ShareEvent.EventId must be set before outbox append");

                var payload = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(shareEvent));
                var recordLength = 4 + 2 + 4 + payload.Length + 4;

                if(currentStream.Length > 0 && currentStream.Length + recordLength > options.SegmentMaxBytes)
                    await RotateAsync(ct);

                var headerBytes = new byte[10];
                var header = headerBytes.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(header[..4], Magic);
                BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], Version);
                BinaryPrimitives.WriteInt32LittleEndian(header[6..10], payload.Length);

                await currentStream.WriteAsync(headerBytes, ct);
                await currentStream.WriteAsync(payload, ct);

                var checksumBytes = new byte[4];
                var checksum = checksumBytes.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(checksum, Checksum(payload));
                await currentStream.WriteAsync(checksumBytes, ct);
            }

            await FlushCoreAsync(flushToDisk, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task FlushAsync(bool flushToDisk, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await FlushCoreAsync(flushToDisk, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task FlushCoreAsync(bool flushToDisk, CancellationToken ct)
    {
        if(flushToDisk)
        {
            currentStream.Flush(true);
            return Task.CompletedTask;
        }

        return currentStream.FlushAsync(ct);
    }

    public async IAsyncEnumerable<ShareEventOutboxRecord> ReadFromCheckpointAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        List<SegmentSnapshot> snapshot = new();

        await gate.WaitAsync(ct);
        try
        {
            await FlushCoreAsync(false, ct);

            var checkpoint = ShareEventOutboxCheckpoint.Load(options.Directory);
            foreach(var segment in GetSegments())
            {
                ct.ThrowIfCancellationRequested();

                if(!string.IsNullOrEmpty(checkpoint.SegmentName) &&
                   string.Compare(segment.Name, checkpoint.SegmentName, StringComparison.Ordinal) < 0)
                    continue;

                var startOffset = segment.Name == checkpoint.SegmentName ? checkpoint.Offset : 0;
                if(startOffset < segment.Length)
                    snapshot.Add(new SegmentSnapshot(segment.FullName, segment.Name, startOffset, segment.Length));
            }
        }
        finally
        {
            gate.Release();
        }

        foreach(var segment in snapshot)
        {
            foreach(var record in ShareEventOutboxReader.ReadSegmentRecords(segment.Path, segment.StartOffset, segment.EndOffset))
            {
                ct.ThrowIfCancellationRequested();
                yield return record;
            }
        }
    }

    public async Task AdvanceCheckpointAsync(ShareEventOutboxRecord record, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ShareEventOutboxCheckpoint.Save(options.Directory, new ShareEventOutboxCheckpoint
            {
                SegmentName = record.SegmentName,
                Offset = record.NextOffset
            });

            DeletePublishedSegments(record.SegmentName);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if(currentStream != null)
            await currentStream.DisposeAsync();
    }

    private void OpenLastSegment()
    {
        var last = GetSegments().LastOrDefault();
        currentSegmentIndex = last == null ? 1 : ParseSegmentIndex(last.Name);
        currentSegmentName = SegmentName(currentSegmentIndex);
        currentStream = new FileStream(Path.Combine(options.Directory, currentSegmentName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        currentStream.Seek(0, SeekOrigin.End);
    }

    private async Task RotateAsync(CancellationToken ct)
    {
        currentStream.Flush(true);
        await currentStream.DisposeAsync();
        currentSegmentIndex++;
        currentSegmentName = SegmentName(currentSegmentIndex);
        currentStream = new FileStream(Path.Combine(options.Directory, currentSegmentName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
    }

    private IEnumerable<FileInfo> GetSegments()
    {
        return new DirectoryInfo(options.Directory)
            .EnumerateFiles("*.wal")
            .OrderBy(x => x.Name, StringComparer.Ordinal);
    }

    private void DeletePublishedSegments(string currentPublishedSegment)
    {
        foreach(var segment in GetSegments())
        {
            if(segment.Name == currentPublishedSegment)
                break;

            if(segment.Name != currentSegmentName)
                segment.Delete();
        }
    }

    private static string SegmentName(long index) => $"{index:D20}.wal";

    private static long ParseSegmentIndex(string segmentName)
    {
        return long.TryParse(Path.GetFileNameWithoutExtension(segmentName), out var value) ? value : 1;
    }

    private sealed record SegmentSnapshot(string Path, string Name, long StartOffset, long EndOffset);

    internal static uint Checksum(byte[] payload)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;

        foreach(var b in payload)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
