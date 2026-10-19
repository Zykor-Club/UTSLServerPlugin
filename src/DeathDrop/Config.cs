using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeathDrop;

public sealed class DeathDropConfig
{
    public sealed class Monster
    {
        [JsonPropertyName("生物id")] public int NpcId { get; set; }
        [JsonPropertyName("完全随机掉落")] public bool FullRandom { get; set; }
        [JsonPropertyName("完全随机掉落排除物品ID")] public List<int> Excluded { get; set; } = [];
        [JsonPropertyName("普通随机掉落物")] public List<int> Pool { get; set; } = [];
        [JsonPropertyName("随机掉落数量最小值")] public int Min { get; set; } = 1;
        [JsonPropertyName("随机掉落数量最大值")] public int Max { get; set; } = 1;
        [JsonPropertyName("掉落概率")] public int Chance { get; set; } = 100;
    }

    [JsonPropertyName("是否开启随机掉落")] public bool RandomDrops { get; set; }
    [JsonPropertyName("完全随机掉落")] public bool FullRandom { get; set; }
    [JsonPropertyName("完全随机掉落排除物品ID")] public List<int> Excluded { get; set; } = [];
    [JsonPropertyName("普通随机掉落物")] public List<int> Pool { get; set; } = [];
    [JsonPropertyName("随机掉落概率")] public int RandomChance { get; set; } = 100;
    [JsonPropertyName("随机掉落数量最小值")] public int Min { get; set; } = 1;
    [JsonPropertyName("随机掉落数量最大值")] public int Max { get; set; } = 1;
    [JsonPropertyName("是否开启自定义掉落")] public bool CustomDrops { get; set; }
    [JsonPropertyName("自定义掉落设置")] public List<Monster> Custom { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DeathDropConfig Load(string path)
    {
        DeathDropConfig cfg = new();
        try
        {
            if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<DeathDropConfig>(File.ReadAllText(path), Json) ?? new(); }
        }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[DeathDrop] 配置解析失败: " + ex.Message); }
        try
        {
            string? d = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(d)) { Directory.CreateDirectory(d); }
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json));
        }
        catch { }
        return cfg;
    }
}
