using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossArena;

/// <summary>Boss 场地配置（config/BossArena/settings.json）。</summary>
public sealed class BossArenaConfig
{
    /// <summary>假世界互通：是否接受玩家从【另一个假世界】直接进入场地（开 = 壶里可直接 /ba 过来）。</summary>
    [JsonPropertyName("互通_允许从假世界进入")]
    public bool InteropAllowEnter { get; set; } = true;

    /// <summary>互通白名单：允许的来源玩法标识（约定用插件名，如 ["PocketWorld"]）。空数组 = 不限制来源玩法。</summary>
    [JsonPropertyName("互通_允许的来源")]
    public string[] InteropAllowedSources { get; set; } = [];

    /// <summary>
    /// 【调试开关】开启后本插件会订阅核心的 Transferring 事件并否决所有进入场地的请求。
    /// 用途：可复现地验证"可否决事件"这条路径（O3）。默认 false，生产请保持关闭。
    /// </summary>
    [JsonPropertyName("调试_否决进入")]
    public bool DebugVetoEnter { get; set; } = false;

    /// <summary>闲置多久后卸载场地（秒）；0 = 常驻。没人时到点存档并卸载，下次 /ba 自动重建。</summary>
    [JsonPropertyName("闲置销毁_秒")]
    public int IdleDestroySeconds { get; set; } = 300;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BossArenaConfig Load(string path)
    {
        BossArenaConfig config = new();
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config = JsonSerializer.Deserialize<BossArenaConfig>(File.ReadAllText(path), JsonOptions) ?? new BossArenaConfig();
            }
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error("[BossArena] 配置解析失败，使用默认值: " + ex.Message);
            config = new BossArenaConfig();
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
