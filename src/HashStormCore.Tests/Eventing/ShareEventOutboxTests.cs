using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HashStormCore.Contracts.Eventing;
using HashStormCore.Eventing.Outbox;
using Xunit;

namespace HashStormCore.Tests.Eventing;

public class ShareEventOutboxTests : IDisposable
{
    public ShareEventOutboxTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "hashstorm-outbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    private readonly string directory;

    [Fact]
    public async Task AppendAndRecoverReadsValidRecords()
    {
        await using var outbox = new FileShareEventOutbox(Options());
        await outbox.AppendAsync(new[] { Event("1"), Event("2") }, false, CancellationToken.None);

        var records = ShareEventOutboxReader.ReadSegment(Path.Combine(directory, "00000000000000000001.wal"));

        Assert.Equal(2, records.Count);
        Assert.Equal("1", records[0].Event.Miner);
        Assert.Equal("2", records[1].Event.Miner);
    }

    [Fact]
    public async Task RecoveryIgnoresPartialTail()
    {
        await using(var outbox = new FileShareEventOutbox(Options()))
            await outbox.AppendAsync(new[] { Event("valid") }, false, CancellationToken.None);

        var path = Path.Combine(directory, "00000000000000000001.wal");
        await File.AppendAllTextAsync(path, "partial");

        ShareEventOutboxRecovery.Recover(directory);
        var records = ShareEventOutboxReader.ReadSegment(path);

        Assert.Single(records);
        Assert.Equal("valid", records[0].Event.Miner);
    }

    [Fact]
    public async Task RecoveryTruncatesCorruptFirstRecordToEmptySegment()
    {
        var path = Path.Combine(directory, "00000000000000000001.wal");
        await File.WriteAllTextAsync(path, "corrupt");

        ShareEventOutboxRecovery.Recover(directory);

        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Empty(ShareEventOutboxReader.ReadSegment(path));
    }

    [Fact]
    public async Task CheckpointAllowsRepublishAfterCrashBeforeAdvance()
    {
        await using var outbox = new FileShareEventOutbox(Options());
        await outbox.AppendAsync(new[] { Event("1") }, false, CancellationToken.None);

        var firstRead = await ReadOne(outbox);
        var secondRead = await ReadOne(outbox);

        Assert.Equal(firstRead.Event.EventId, secondRead.Event.EventId);
    }

    [Fact]
    public async Task ReadFromCheckpointCanBeStoppedAtPublisherBatchSize()
    {
        await using var outbox = new FileShareEventOutbox(Options());
        await outbox.AppendAsync(Enumerable.Range(1, 50).Select(x => Event(x.ToString())).ToArray(), false, CancellationToken.None);

        var records = new List<ShareEventOutboxRecord>();
        await foreach(var record in outbox.ReadFromCheckpointAsync(CancellationToken.None))
        {
            records.Add(record);
            if(records.Count == 7)
                break;
        }

        Assert.Equal(7, records.Count);
        Assert.Equal("1", records[0].Event.Miner);
        Assert.Equal("7", records[^1].Event.Miner);
    }

    public void Dispose()
    {
        if(Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private ShareEventOutboxOptions Options() => new()
    {
        Directory = directory,
        SegmentMaxBytes = 1024 * 1024
    };

    private static ShareEvent Event(string miner) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventType = ShareEventType.ShareAccepted,
        PoolId = "pool",
        Miner = miner,
        Created = DateTime.UtcNow,
        Difficulty = 1
    };

    private static async Task<ShareEventOutboxRecord> ReadOne(FileShareEventOutbox outbox)
    {
        await foreach(var record in outbox.ReadFromCheckpointAsync(CancellationToken.None))
            return record;

        throw new InvalidOperationException("No record");
    }
}
