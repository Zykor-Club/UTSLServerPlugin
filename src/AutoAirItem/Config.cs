using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoAirItem;

public sealed class AutoAirConfig
{
    [JsonPropertyName("插件开关")] public bool Enable { get; set; } = true;
    [JsonPropertyName("排除物品")] public int[] Exclude { get; set; } = [71, 72, 73, 74];
    [JsonPropertyName("玩家数据")] public List<PlayerData> Players { get; set; } = [];

    public sealed class PlayerData
    {
        [JsonPropertyName("名字")] public string Name { get; set; } = "";
        [JsonPropertyName("启用")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("自动学习")] public bool Auto { get; set; } = true;
        [JsonPropertyName("提示")] public bool Mess { get; set; } = true;
        [JsonPropertyName("垃圾表")] public List<int> Types { get; set; } = [];
        [JsonPropertyName("已清理")] public Dictionary<int, int> Deleted { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AutoAirConfig Load(string path)
    {
        AutoAirConfig cfg = new();
        try { if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<AutoAirConfig>(File.ReadAllText(path), Json) ?? new(); } }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[AutoAirItem] 配置解析失败: " + ex.Message); }
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

    public PlayerData For(string name)
    {
        PlayerData? p = Players.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null)
        {
            p = new PlayerData { Name = name };
            Players.Add(p);
        }
        return p;
    }
}
