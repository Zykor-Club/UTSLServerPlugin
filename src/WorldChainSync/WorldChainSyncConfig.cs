using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorldChainSync;

/// <summary>
/// 世界链状态同步配置（config/WorldChainSync/WorldChainSync.json）。
/// 「世界链」里的世界会被视为同一张大地图的不同切片：时间/天气/进度必须一致。
/// </summary>
public sealed class WorldChainSyncConfig
{
    /// <summary>世界名顺序，必须与 config.json 里 autoStartServers.name 一致（EdgeStitch 的世界链建议保持一致）。</summary>
    [JsonPropertyName("世界链")]
    public List<string> WorldChain { get; set; } = [];

    /// <summary>检测"玩家换乘"的间隔（毫秒）。越小越即时，250ms 足够。</summary>
    [JsonPropertyName("检测间隔_毫秒")]
    public int DetectIntervalMs { get; set; } = 250;

    /// <summary>周期性统一的间隔（毫秒）：以"有玩家的世界"为权威推给其它世界。</summary>
    [JsonPropertyName("统一间隔_毫秒")]
    public int UnifyIntervalMs { get; set; } = 2000;

    /// <summary>同步内容开关：每一组都可以单独关掉。</summary>
    [JsonPropertyName("同步内容")]
    public SyncContentConfig SyncContent { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static WorldChainSyncConfig Load(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

        WorldChainSyncConfig config = new();
        if (File.Exists(path))
        {
            try { config = JsonSerializer.Deserialize<WorldChainSyncConfig>(File.ReadAllText(path), JsonOptions) ?? new WorldChainSyncConfig(); }
            catch (Exception ex) { TShockAPI.TShock.Log.Error("[WorldChainSync] 配置解析失败，使用默认值: " + ex.Message); config = new WorldChainSyncConfig(); }
        }
        if (config.WorldChain.Count == 0) { config.WorldChain = ["West", "Dev", "East"]; }
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        return config;
    }
}

/// <summary>同步内容分组（关掉即不同步该组）。</summary>
public sealed class SyncContentConfig
{
    [JsonPropertyName("时间")] public bool Time { get; set; } = true;
    [JsonPropertyName("天气")] public bool Weather { get; set; } = true;
    [JsonPropertyName("血月")] public bool BloodMoon { get; set; } = true;
    [JsonPropertyName("日食")] public bool Eclipse { get; set; } = true;
    [JsonPropertyName("南瓜月")] public bool PumpkinMoon { get; set; } = true;
    [JsonPropertyName("霜月")] public bool SnowMoon { get; set; } = true;
    [JsonPropertyName("史莱姆雨")] public bool SlimeRain { get; set; } = true;
    [JsonPropertyName("渔夫任务")] public bool AnglerQuest { get; set; } = true;
    [JsonPropertyName("入侵")] public bool Invasion { get; set; } = true;
    [JsonPropertyName("困难模式")] public bool HardMode { get; set; } = true;
    [JsonPropertyName("Boss进度")] public bool BossProgress { get; set; } = true;
    [JsonPropertyName("已解救NPC")] public bool SavedNpc { get; set; } = true;
}
