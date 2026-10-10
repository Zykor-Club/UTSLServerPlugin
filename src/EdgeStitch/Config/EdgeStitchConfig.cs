using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeStitch.Config;

/// <summary>
/// EdgeStitch 配置。
///
/// 「世界链」决定谁接在谁旁边：链中第 i 个世界的【东边缘】出去 → 第 i+1 个世界的【西边缘】。
/// 例如 ["West", "Dev", "East"] 表示 West — Dev — East 首尾相接成一条链。
/// </summary>
public sealed class EdgeStitchConfig
{
    /// <summary>世界名顺序，必须与 config.json 里的 autoStartServers.name 一致。</summary>
    [JsonPropertyName("世界链")]
    public List<string> WorldChain { get; set; } = [];

    /// <summary>链的两端是否相连（West 的西边直接通到 East 的东边），可做成环。</summary>
    [JsonPropertyName("首尾相连成环")]
    public bool Loop { get; set; }

    /// <summary>边缘带宽度（图格）。未配置「陆地边缘」的世界用它：距世界边缘这么多格内触发。</summary>
    [JsonPropertyName("边缘带宽度_格")]
    public int EdgeBandTiles { get; set; } = 64;

    /// <summary>同一玩家的两次换乘最小间隔，防止在接缝处来回弹。</summary>
    [JsonPropertyName("传送冷却_毫秒")]
    public int CooldownMs { get; set; } = 2500;

    /// <summary>是否只在玩家朝世界外侧移动时才触发（强烈建议开启）。</summary>
    [JsonPropertyName("仅朝外移动时触发")]
    public bool RequireOutwardVelocity { get; set; } = true;

    /// <summary>
    /// 各世界「可通行区域的陆地边缘」（图格 X 坐标）。
    ///
    /// 抹海缝合之后，世界两端是【虚空】而不是海洋，换乘边界不能再按"世界边缘"算，
    /// 必须按各世界自己的陆地边缘算 —— 而且每个世界、每一侧都不同（海洋宽度不一样）。
    /// 没配置的世界退回「边缘带宽度_格」的旧行为。
    /// </summary>
    [JsonPropertyName("陆地边缘")]
    public Dictionary<string, WorldLandEdges> LandEdges { get; set; } = [];

    /// <summary>提前多少格触发换乘：玩家还没走到陆地边缘就被传送走，绝不会踏进虚空。</summary>
    [JsonPropertyName("边缘触发余量_格")]
    public int TriggerMarginTiles { get; set; } = 24;

    /// <summary>
    /// 切片间距（格）。>0 时启用「切片模式」落点：
    /// 目标落点 = 源坐标 ∓ 切片间距，保证换乘前后【大地图坐标一致】，画面不跳。
    /// （不重叠的切片之间，对应坐标是负数，必然断裂；重叠切分 + 这个间距才能连续。）
    /// 东向传送用减法，西向用加法。0 = 关闭，退回按对方陆地边缘落点。
    /// </summary>
    [JsonPropertyName("切片间距_格")]
    public int SliceDeltaTiles { get; set; }

    /// <summary>按世界名取陆地边缘（大小写不敏感）。</summary>
    public WorldLandEdges? Resolve(string worldName)
    {
        foreach (KeyValuePair<string, WorldLandEdges> kv in LandEdges)
        {
            if (string.Equals(kv.Key, worldName, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }
        return null;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static EdgeStitchConfig Load(string path)
    {
        // 插件的配置目录不一定预先存在，先确保建好
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
            config.WorldChain = ["West", "Dev", "East"];
        }

        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }
}

/// <summary>某个世界两侧的陆地边缘（图格 X）。null 表示该侧没有接缝（整张大地图的外端）。</summary>
public sealed class WorldLandEdges
{
    [JsonPropertyName("西")]
    public int? West { get; set; }

    [JsonPropertyName("东")]
    public int? East { get; set; }
}
