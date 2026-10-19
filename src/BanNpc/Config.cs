using System.Text.Json;
using System.Text.Json.Serialization;

namespace BanNpc;

public sealed class BanNpcConfig
{
    [JsonPropertyName("阻止怪物生成表")] public List<int> Npcs { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BanNpcConfig Load(string path)
    {
        BanNpcConfig cfg = new();
        try { if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<BanNpcConfig>(File.ReadAllText(path), Json) ?? new(); } }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[BanNpc] 配置解析失败: " + ex.Message); }
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
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[BanNpc] 保存失败: " + ex.Message); }
    }
}
