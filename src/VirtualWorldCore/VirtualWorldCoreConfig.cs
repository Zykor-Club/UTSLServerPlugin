using System.Text.Json;
using System.Text.Json.Serialization;

namespace UTSL.VirtualWorld;

/// <summary>核心配置（config/VirtualWorldCore/settings.json）。默认值 = 拆分前的硬编码值，升级不改变行为。</summary>
public sealed class VirtualWorldCoreConfig
{
    [JsonPropertyName("模板_最大宽")] public int TemplateMaxWidth { get; set; } = 1500;
    [JsonPropertyName("模板_最大高")] public int TemplateMaxHeight { get; set; } = 1300;
    [JsonPropertyName("模板_最大文件MB")] public int TemplateMaxFileMB { get; set; } = 64;
    [JsonPropertyName("实例_最多同时存在")] public int MaxInstances { get; set; }
    [JsonPropertyName("卸载_等待世界线程秒")] public int DestroyPostTimeoutSeconds { get; set; } = 10;
    [JsonPropertyName("卸载_关闭世界秒")] public int DestroyCloseTimeoutSeconds { get; set; } = 15;
    [JsonPropertyName("加载_等待秒")] public int LoadTimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("诊断日志")] public bool Diagnostics { get; set; } = true;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static VirtualWorldCoreConfig Load(string path)
    {
        VirtualWorldCoreConfig config = new();
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config = JsonSerializer.Deserialize<VirtualWorldCoreConfig>(File.ReadAllText(path), JsonOptions) ?? new VirtualWorldCoreConfig();
            }
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error("[VirtualWorldCore] 配置解析失败，使用默认值: " + ex.Message);
            config = new VirtualWorldCoreConfig();
        }

        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
            }
        }
        catch { }

        return config;
    }
}
