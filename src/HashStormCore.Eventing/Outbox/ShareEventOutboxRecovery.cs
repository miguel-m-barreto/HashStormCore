namespace HashStormCore.Eventing.Outbox;

public static class ShareEventOutboxRecovery
{
    public static void Recover(string directory)
    {
        if(!Directory.Exists(directory))
            return;

        foreach(var segment in new DirectoryInfo(directory).EnumerateFiles("*.wal").OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var result = ShareEventOutboxReader.ReadSegmentWithResult(segment.FullName);

            if(!result.IsComplete)
            {
                using var stream = new FileStream(segment.FullName, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength(result.ValidLength);
                break;
            }
        }
    }
}
