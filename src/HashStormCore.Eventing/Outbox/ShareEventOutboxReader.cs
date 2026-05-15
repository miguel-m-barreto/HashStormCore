using System.Buffers.Binary;
using HashStormCore.Contracts.Eventing;
using Newtonsoft.Json;

namespace HashStormCore.Eventing.Outbox;

public static class ShareEventOutboxReader
{
    public static IReadOnlyList<ShareEventOutboxRecord> ReadSegment(string path, long startOffset = 0)
    {
        return ReadSegmentWithResult(path, startOffset).Records;
    }

    public static IEnumerable<ShareEventOutboxRecord> ReadSegmentRecords(string path, long startOffset = 0, long? endOffset = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = Math.Min(Math.Max(0, startOffset), stream.Length);
        var readLimit = Math.Min(endOffset ?? stream.Length, stream.Length);

        var header = new byte[10];
        var checksumBytes = new byte[4];
        var segmentName = Path.GetFileName(path);

        while(stream.Position < readLimit)
        {
            if(!TryReadRecord(stream, readLimit, segmentName, header, checksumBytes, out var record))
                yield break;

            yield return record;
        }
    }

    public static ShareEventOutboxReadResult ReadSegmentWithResult(string path, long startOffset = 0)
    {
        var records = new List<ShareEventOutboxRecord>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = Math.Min(Math.Max(0, startOffset), stream.Length);
        var validLength = stream.Position;
        var stoppedCleanly = true;

        var header = new byte[10];
        var checksumBytes = new byte[4];
        var segmentName = Path.GetFileName(path);

        while(stream.Position < stream.Length)
        {
            if(!TryReadRecord(stream, stream.Length, segmentName, header, checksumBytes, out var record))
            {
                stoppedCleanly = false;
                break;
            }

            validLength = stream.Position;
            records.Add(record);
        }

        return new ShareEventOutboxReadResult(records, validLength, stoppedCleanly && validLength == stream.Length);
    }

    private static bool TryReadRecord(Stream stream, long readLimit, string segmentName, byte[] header, byte[] checksumBytes, out ShareEventOutboxRecord record)
    {
        record = null!;
        var offset = stream.Position;

        if(!ReadExact(stream, header))
            return false;

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan()[..4]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan()[4..6]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan()[6..10]);

        if(magic != FileShareEventOutbox.Magic || version != FileShareEventOutbox.Version || payloadLength <= 0)
            return false;

        if(readLimit - stream.Position < payloadLength + 4)
            return false;

        var payload = new byte[payloadLength];
        if(stream.Read(payload, 0, payload.Length) != payload.Length)
            return false;

        if(!ReadExact(stream, checksumBytes))
            return false;

        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
        if(checksum != FileShareEventOutbox.Checksum(payload))
            return false;

        var shareEvent = JsonConvert.DeserializeObject<ShareEvent>(System.Text.Encoding.UTF8.GetString(payload));
        if(shareEvent == null)
            return false;

        record = new ShareEventOutboxRecord
        {
            SegmentName = segmentName,
            Offset = offset,
            NextOffset = stream.Position,
            Event = shareEvent
        };

        return true;
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while(total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if(read == 0)
                return false;

            total += read;
        }

        return true;
    }
}

public sealed record ShareEventOutboxReadResult(IReadOnlyList<ShareEventOutboxRecord> Records, long ValidLength, bool IsComplete);
