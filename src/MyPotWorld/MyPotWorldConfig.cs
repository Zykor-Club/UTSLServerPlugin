using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPotWorld;

/// <summary>尘歌壶配置（config/MyPotWorld/settings.json）。</summary>
public sealed class MyPotWorldConfig
{
    [JsonPropertyName("启用")]
    public bool Enabled { get; set; } = true;

    /// <summary>同时存在的尘歌壶上限（内存保护）。实测：每个常驻壶 4200x2400 约 220~260MB。</summary>
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

    // ===== 家园：活动区域（越界传送回；半径可随等级扩大）=====

    /// <summary>是否启用活动区域限制。</summary>
    [JsonPropertyName("家园_活动区域_启用")]
    public bool AreaEnabled { get; set; } = true;

    /// <summary>
    /// 家园地面的 Y（图格坐标）。空世界没有真地表，这里给定一个"地表高度"：
    /// 默认 500（≈ 主世界地表，既不高到太空也不低到地下）。会按活动区域大小自动夹紧。
    /// </summary>
    [JsonPropertyName("家园_地面Y")]
    public int HomeFloorY { get; set; } = 500;

    /// <summary>
    /// 【服主自定义】每一级的活动区域边长（格）。索引 = 等级（0 起），第 0 项就是初始范围。
    /// 加载时会强制"非递减"（下一级不得小于上一级）且不小于 8。
    /// </summary>
    [JsonPropertyName("家园_活动区域_各级边长")]
    public int[] AreaSizes { get; set; } = [150, 200, 260, 330, 420];

    /// <summary>越界检查间隔（毫秒）。越小越"立即"。</summary>
    [JsonPropertyName("家园_活动区域_检查间隔毫秒")]
    public int AreaCheckIntervalMs { get; set; } = 300;

    /// <summary>越界后落在离地面多少格处（默认 2）。</summary>
    [JsonPropertyName("家园_活动区域_回传落点偏移")]
    public int AreaReturnOffset { get; set; } = 2;

    /// <summary>圈墙图格 ID（默认 273 = 石板）。</summary>
    [JsonPropertyName("家园_活动区域_圈墙图格ID")]
    public int AreaWallTileId { get; set; } = 273;

    /// <summary>圈墙油漆 ID（默认 30 = 反色漆；-1 = 不刷漆）。</summary>
    [JsonPropertyName("家园_活动区域_圈墙油漆ID")]
    public int AreaWallPaintId { get; set; } = 30;

    /// <summary>升级消耗的物品 ID（0 = 不消耗）。</summary>
    [JsonPropertyName("家园_活动区域_升级消耗物品ID")]
    public int AreaUpgradeItemId { get; set; } = 0;

    /// <summary>升级基础消耗数量（等级 0 → 1）。</summary>
    [JsonPropertyName("家园_活动区域_升级消耗基础数量")]
    public int AreaUpgradeBaseCount { get; set; } = 0;

    /// <summary>每升一级额外增加的消耗数量。</summary>
    [JsonPropertyName("家园_活动区域_升级消耗每级递增")]
    public int AreaUpgradeStepCount { get; set; } = 0;




    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static MyPotWorldConfig Load(string path)
    {
        MyPotWorldConfig config = new();
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config = JsonSerializer.Deserialize<MyPotWorldConfig>(File.ReadAllText(path), JsonOptions) ?? new MyPotWorldConfig();
            }
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error("[尘歌壶] 配置解析失败，使用默认值: " + ex.Message);
            config = new MyPotWorldConfig();
        }

        // ★ 活动区域：强制"下一级不得小于上一级"，且每级至少 8 格
        try
        {
            int[] src = config.AreaSizes ?? [];
            if (src.Length == 0)
            {
                config.AreaSizes = [150];
            }
            else
            {
                int[] fixedSizes = new int[src.Length];
                int prev = 0;
                for (int i = 0; i < src.Length; i++)
                {
                    int v = Math.Max(8, src[i]);
                    if (v < prev)
                    {
                        TShockAPI.TShock.Log.Warning($"[尘歌壶] 活动区域第 {i} 级边长 {v} 小于上一级 {prev}，已自动抬到 {prev}");
                        v = prev;
                    }
                    fixedSizes[i] = v;
                    prev = v;
                }
                config.AreaSizes = fixedSizes;
            }
        }
        catch { }

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
