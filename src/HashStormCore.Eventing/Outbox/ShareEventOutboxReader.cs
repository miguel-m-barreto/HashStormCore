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
        while(stream.Position < readLimit)
        {
            var offset = stream.Position;
            if(!ReadExact(stream, header))
                yield break;

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan()[..4]);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan()[4..6]);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan()[6..10]);

            if(magic != FileShareEventOutbox.Magic || version != FileShareEventOutbox.Version || payloadLength <= 0)
                yield break;

            if(readLimit - stream.Position < payloadLength + 4)
                yield break;

            var payload = new byte[payloadLength];
            if(stream.Read(payload, 0, payload.Length) != payload.Length)
                yield break;

            var checksumBytes = new byte[4];
            if(!ReadExact(stream, checksumBytes))
                yield break;

            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
            if(checksum != FileShareEventOutbox.Checksum(payload))
                yield break;

            var shareEvent = JsonConvert.DeserializeObject<ShareEvent>(System.Text.Encoding.UTF8.GetString(payload));
            if(shareEvent == null)
                yield break;

            yield return new ShareEventOutboxRecord
            {
                SegmentName = Path.GetFileName(path),
                Offset = offset,
                NextOffset = stream.Position,
                Event = shareEvent
            };
        }
    }

    public static ShareEventOutboxReadResult ReadSegmentWithResult(string path, long startOffset = 0)
    {
        var records = new List<ShareEventOutboxRecord>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = Math.Min(Math.Max(0, startOffset), stream.Length);
        var validLength = stream.Position;
        var stoppedCleanly = true;

        Span<byte> header = stackalloc byte[10];
        while(stream.Position < stream.Length)
        {
            var offset = stream.Position;
            if(!ReadExact(stream, header))
            {
                stoppedCleanly = false;
                break;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..6]);
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[6..10]);

            if(magic != FileShareEventOutbox.Magic || version != FileShareEventOutbox.Version || payloadLength <= 0)
            {
                stoppedCleanly = false;
                break;
            }

            if(stream.Length - stream.Position < payloadLength + 4)
            {
                stoppedCleanly = false;
                break;
            }

            var payload = new byte[payloadLength];
            if(stream.Read(payload, 0, payload.Length) != payload.Length)
            {
                stoppedCleanly = false;
                break;
            }

            var checksumBytes = new byte[4];
            if(!ReadExact(stream, checksumBytes))
            {
                stoppedCleanly = false;
                break;
            }

            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(checksumBytes);
            if(checksum != FileShareEventOutbox.Checksum(payload))
            {
                stoppedCleanly = false;
                break;
            }

            var shareEvent = JsonConvert.DeserializeObject<ShareEvent>(System.Text.Encoding.UTF8.GetString(payload));
            if(shareEvent == null)
            {
                stoppedCleanly = false;
                break;
            }

            validLength = stream.Position;
            records.Add(new ShareEventOutboxRecord
            {
                SegmentName = Path.GetFileName(path),
                Offset = offset,
                NextOffset = stream.Position,
                Event = shareEvent
            });
        }

        return new ShareEventOutboxReadResult(records, validLength, stoppedCleanly && validLength == stream.Length);
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
