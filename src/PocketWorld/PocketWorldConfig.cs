using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketWorld;

/// <summary>尘歌壶配置（config/PocketWorld/settings.json）。</summary>
public sealed class PocketWorldConfig
{
    [JsonPropertyName("启用")]
    public bool Enabled { get; set; } = true;

    /// <summary>同时存在的尘歌壶上限（内存保护；每个壶约 100~170MB）。</summary>
    [JsonPropertyName("最大同时存在数")]
    public int MaxPots { get; set; } = 6;

    /// <summary>壶里没人多久（秒）后自动存档并销毁；0 = 不自动销毁。</summary>
    [JsonPropertyName("闲置销毁_秒")]
    public int IdleDestroySeconds { get; set; } = 600;

    /// <summary>假世界互通：是否接受玩家从【另一个假世界】直接进入壶（开 = 场地等地可直接 /cg 过来）。</summary>
    [JsonPropertyName("互通_允许从假世界进入")]
    public bool InteropAllowEnter { get; set; } = true;

    /// <summary>互通白名单：允许的来源玩法标识（约定用插件名，如 ["BossArena"]）。空数组 = 不限制来源玩法。</summary>
    [JsonPropertyName("互通_允许的来源")]
    public string[] InteropAllowedSources { get; set; } = [];




    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PocketWorldConfig Load(string path)
    {
        PocketWorldConfig config = new();
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config = JsonSerializer.Deserialize<PocketWorldConfig>(File.ReadAllText(path), JsonOptions) ?? new PocketWorldConfig();
            }
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error("[尘歌壶] 配置解析失败，使用默认值: " + ex.Message);
            config = new PocketWorldConfig();
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
