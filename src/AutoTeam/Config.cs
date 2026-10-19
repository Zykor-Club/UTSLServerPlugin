using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoTeam;

public sealed class AutoTeamConfig
{
    [JsonPropertyName("开启插件")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("组对应的队伍")] public Dictionary<string, string> GroupTeam { get; set; } = new()
    {
        ["guest"] = "pink",
        ["default"] = "blue",
        ["owner"] = "red",
        ["admin"] = "green",
        ["vip"] = "none",
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AutoTeamConfig Load(string path)
    {
        AutoTeamConfig cfg = new();
        try { if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<AutoTeamConfig>(File.ReadAllText(path), Json) ?? new(); } }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[AutoTeam] 配置解析失败: " + ex.Message); }
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
