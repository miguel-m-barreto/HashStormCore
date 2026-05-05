using Newtonsoft.Json;

namespace HashStormCore.Eventing.Outbox;

public class ShareEventOutboxCheckpoint
{
    public string SegmentName { get; set; } = string.Empty;
    public long Offset { get; set; }

    public static ShareEventOutboxCheckpoint Load(string directory)
    {
        var path = Path.Combine(directory, "publish.checkpoint");
        if(!File.Exists(path))
            return new ShareEventOutboxCheckpoint();

        var text = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<ShareEventOutboxCheckpoint>(text) ?? new ShareEventOutboxCheckpoint();
    }

    public static void Save(string directory, ShareEventOutboxCheckpoint checkpoint)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "publish.checkpoint");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(checkpoint));
        File.Move(tmp, path, true);
    }
}
