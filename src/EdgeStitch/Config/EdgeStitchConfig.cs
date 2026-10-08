using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeStitch.Config;

/// <summary>
/// EdgeStitch 配置。
///
/// 「世界链」决定谁接在谁旁边：链中第 i 个世界的【东边缘】出去 → 第 i+1 个世界的【西边缘】；
/// 反向同理。例如 ["West", "Dev", "East"] 表示 West — Dev — East 首尾相接成一条链。
/// </summary>
public sealed class EdgeStitchConfig
{
    /// <summary>世界名顺序，必须与 config.json 里的 autoStartServers.name 一致。</summary>
    [JsonPropertyName("世界链")]
    public List<string> WorldChain { get; set; } = [];

    /// <summary>链的两端是否相连（West 的西边直接通到 East 的东边），可做成环。</summary>
    [JsonPropertyName("首尾相连成环")]
    public bool Loop { get; set; }

    /// <summary>边缘带宽度（单位：图格）。玩家进入距边缘这么多格的范围内即触发换乘。</summary>
    [JsonPropertyName("边缘带宽度_格")]
    public int EdgeBandTiles { get; set; } = 64;

    /// <summary>同一玩家的两次换乘最小间隔，防止在接缝处来回弹。</summary>
    [JsonPropertyName("传送冷却_毫秒")]
    public int CooldownMs { get; set; } = 2500;

    /// <summary>是否只在玩家朝世界外侧移动时才触发（强烈建议开启）。</summary>
    [JsonPropertyName("仅朝外移动时触发")]
    public bool RequireOutwardVelocity { get; set; } = true;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static EdgeStitchConfig Load(string path)
    {
        // 插件的配置目录不一定预先存在，先确保建好（否则写配置会 DirectoryNotFoundException）
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        EdgeStitchConfig config = new();
        if (File.Exists(path))
        {
            try
            {
                config = JsonSerializer.Deserialize<EdgeStitchConfig>(File.ReadAllText(path), JsonOptions) ?? new EdgeStitchConfig();
            }
            catch (Exception ex)
            {
                TShockAPI.TShock.Log.Error($"[EdgeStitch] 配置解析失败，使用默认值: {ex.Message}");
                config = new EdgeStitchConfig();
            }
        }

        if (config.WorldChain.Count == 0)
        {
            // 未配置时给出一个开箱可用的默认链，方便第一次启动就能看到效果
            config.WorldChain = ["West", "Dev", "East"];
        }

        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }
}
