using System.Text.Json;
using System.Text.Json.Serialization;

namespace DonotFuck;

public sealed class DonotFuckConfig
{
    [JsonPropertyName("每页行数")] public int PageSize { get; set; } = 30;
    [JsonPropertyName("启用日志")] public bool EnableLog { get; set; } = true;
    [JsonPropertyName("脏话表")] public List<string> DirtyWords { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DonotFuckConfig Load(string path)
    {
        DonotFuckConfig cfg = new();
        try { if (File.Exists(path)) { cfg = JsonSerializer.Deserialize<DonotFuckConfig>(File.ReadAllText(path), Json) ?? new(); } }
        catch (Exception ex) { TShockAPI.TShock.Log.Error("[DonotFuck] 配置解析失败: " + ex.Message); }
        if (cfg.DirtyWords.Count == 0) { cfg.DirtyWords.AddRange(["6", "六"]); }
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
