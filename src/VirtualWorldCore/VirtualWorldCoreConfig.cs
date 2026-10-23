using System.Text.Json;
using System.Text.Json.Serialization;

namespace UTSL.VirtualWorld;

/// <summary>
/// 核心配置（config/VirtualWorldCore/settings.json）。
///
/// v8 起模板上限分两层：
///   · 核心只管【资源硬上限】（防 4000×4000 之类把内存/磁盘打爆），默认 0 = 不限；
///   · 玩法自己的尺寸/大小约束走 <see cref="ITemplateGuard"/>（例如尘歌壶的 1500×1300 / 64MB）。
/// 旧版的「模板_最大宽/高/文件MB」已废弃：读旧配置时会被忽略，下次保存自动换成新键。
/// </summary>
public sealed class VirtualWorldCoreConfig
{
    // ===== 资源硬上限（0 = 不限；只防极端，不管玩法）=====

    [JsonPropertyName("模板_硬上限宽")] public int TemplateHardMaxWidth { get; set; }
    [JsonPropertyName("模板_硬上限高")] public int TemplateHardMaxHeight { get; set; }
    [JsonPropertyName("模板_硬上限文件MB")] public int TemplateHardMaxFileMB { get; set; }

    /// <summary>按玩法组（<see cref="IVirtualWorld.Group"/>）覆盖的上限；未列出的组用全局硬上限。</summary>
    [JsonPropertyName("模板_分组上限")]
    public Dictionary<string, TemplateLimits>? TemplateLimitsByGroup { get; set; }

    // ===== 运行时 =====

    [JsonPropertyName("实例_最多同时存在")] public int MaxInstances { get; set; }
    [JsonPropertyName("卸载_等待世界线程秒")] public int DestroyPostTimeoutSeconds { get; set; } = 10;
    [JsonPropertyName("卸载_关闭世界秒")] public int DestroyCloseTimeoutSeconds { get; set; } = 15;
    [JsonPropertyName("加载_等待秒")] public int LoadTimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("诊断日志")] public bool Diagnostics { get; set; } = true;

    /// <summary>取某玩法组实际生效的上限：优先分组表，其次全局硬上限；各项 0 = 该项不限。</summary>
    public TemplateLimits LimitsFor(string? group)
    {
        if (!string.IsNullOrWhiteSpace(group) && TemplateLimitsByGroup is not null
            && TemplateLimitsByGroup.TryGetValue(group.Trim(), out TemplateLimits? own) && own is not null)
        {
            return own;
        }
        return new TemplateLimits
        {
            Width = TemplateHardMaxWidth,
            Height = TemplateHardMaxHeight,
            FileMB = TemplateHardMaxFileMB,
        };
    }

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

/// <summary>一组模板上限（0 = 该项不限）。</summary>
public sealed class TemplateLimits
{
    [JsonPropertyName("宽")] public int Width { get; set; }
    [JsonPropertyName("高")] public int Height { get; set; }
    [JsonPropertyName("文件MB")] public int FileMB { get; set; }
}
