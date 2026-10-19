using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisableMonsLoot;

public sealed class DisableMonsLootConfig
{
    [JsonPropertyName("插件开关")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("是否全杀")] public bool KillAll { get; set; }
    [JsonPropertyName("清理范围")] public int Radius { get; set; } = 35;
    [JsonPropertyName("清理列表")] public List<Group> Groups { get; set; } = [];

    public sealed class Group
    {
        [JsonPropertyName("怪物名")] public string Name { get; set; } = "";
        [JsonPropertyName("清理")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("怪物ID")] public int[] Ids { get; set; } = [];
        [JsonPropertyName("物品ID")] public int[] ItemIds { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DisableMonsLootConfig Load(string path)
    {
        DisableMonsLootConfig cfg = new();
        try
        {
            if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<DisableMonsLootConfig>(File.ReadAllText(path), Json) ?? new(); }
        }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[DisableMonsLoot] 配置解析失败: " + ex.Message); }
        if (cfg.Groups.Count == 0) { cfg.Groups.AddRange(DefaultGroups()); }
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
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[DisableMonsLoot] 配置保存失败: " + ex.Message); }
    }

    private static List<Group> DefaultGroups() =>
    [
        new() { Name = "史莱姆王", Ids = [50], ItemIds = [3318, 2430, 256, 257, 258, 3090] },
        new() { Name = "克苏鲁之眼", Ids = [4], ItemIds = [3319, 3262, 3097] },
        new() { Name = "世界吞噬者/克脑", Ids = [13, 14, 15, 266], ItemIds = [56, 86, 880, 1329, 3320, 3321] },
        new() { Name = "蜂王", Ids = [222], ItemIds = [3322, 1123, 2888, 1121, 1132, 1130] },
        new() { Name = "骷髅王", Ids = [35, 36], ItemIds = [154, 327, 3323, 346, 273, 329] },
        new() { Name = "独眼巨鹿", Ids = [668], ItemIds = [3320, 5098, 5095, 5117, 5118, 5119] },
    ];
}
