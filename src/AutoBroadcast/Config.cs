using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoBroadcast;

public sealed class AutoBroadcastConfig
{
    [JsonPropertyName("广播列表")] public List<BroadcastEntry> Broadcasts { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AutoBroadcastConfig Load(string path)
    {
        AutoBroadcastConfig cfg = new();
        try { if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<AutoBroadcastConfig>(File.ReadAllText(path), Json) ?? new(); } }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[AutoBroadcast] 配置解析失败: " + ex.Message); }
        if (cfg.Broadcasts.Count == 0)
        {
            cfg.Broadcasts.Add(new BroadcastEntry
            {
                Name = "示例广播",
                Enabled = false,
                Messages = ["/say Ciallo～(∠・ω< )⌒★", "自动广播执行了服务器指令"],
                ColorRgb = [255, 234, 115],
                Interval = 600,
            });
        }
        cfg.Save(path);
        return cfg;
    }

    public void Save(string path)
    {
        try
        {
            string? d = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(d)) { Directory.CreateDirectory(d); }
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}

public sealed class BroadcastEntry
{
    [JsonPropertyName("广播名称")] public string Name { get; set; } = "";
    [JsonPropertyName("启用")] public bool Enabled { get; set; }
    [JsonPropertyName("广播消息")] public string[] Messages { get; set; } = [];
    [JsonPropertyName("RGB颜色")] public int[] ColorRgb { get; set; } = [255, 255, 255];
    [JsonPropertyName("时间间隔")] public int Interval { get; set; }
    [JsonPropertyName("延迟执行")] public int StartDelay { get; set; }
    [JsonPropertyName("广播组")] public string[] Groups { get; set; } = [];
    [JsonPropertyName("触发词语")] public string[] TriggerWords { get; set; } = [];
    [JsonPropertyName("触发整个组")] public bool TriggerToWholeGroup { get; set; }

    [JsonIgnore] internal int Timer;
    [JsonIgnore] internal bool Started;
    [JsonIgnore] internal int DelayLeft;
}
