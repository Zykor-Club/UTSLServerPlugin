using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPotWorld;

/// <summary>玩家的个人开关（保持联系 / 同步状态），按玩家标识持久化到 players.json。</summary>
public sealed class PlayerPrefs
{
    [JsonPropertyName("保持联系")] public bool Contact { get; set; }
    [JsonPropertyName("同步状态")] public bool Sync { get; set; }

    /// <summary>家园活动区域的等级（每级 +配置增量 半径）。</summary>
    [JsonPropertyName("活动区域等级")] public int AreaLevel { get; set; }

    /// <summary>壶内禁止自然刷怪（默认开；按壶主独立）。</summary>
    [JsonPropertyName("禁止刷怪")] public bool NoSpawn { get; set; } = true;

    /// <summary>壶内无敌（默认开；离开壶时由插件清除）。</summary>
    [JsonPropertyName("无敌")] public bool God { get; set; } = true;

    /// <summary>
    /// 建壶时用的【整世界模板基线】名（对应 Worlds/_vwc_templates/&lt;名&gt;_&lt;宽&gt;x&lt;高&gt;.wld）。
    /// 空 = 用默认 pot_blank。建房请用 /cg save + /cg paste（区块模板），这个只影响"新壶初始内容"。
    /// </summary>
    [JsonPropertyName("模板")] public string Template { get; set; } = "";

    /// <summary>
    /// 自定义出生点：相对【活动区域中心】的图格偏移；int.MinValue = 未设置（默认）。
    /// ★ 存偏移而不是绝对坐标：区域升级扩大后不会突然落到圈墙外面。
    /// </summary>
    [JsonPropertyName("出生点_X")] public int SpawnOffX { get; set; } = int.MinValue;
    [JsonPropertyName("出生点_Y")] public int SpawnOffY { get; set; } = int.MinValue;

    [JsonIgnore] public bool HasSpawn => SpawnOffX != int.MinValue && SpawnOffY != int.MinValue;

    /// <summary>已选定的建筑模板名；空 = 未选择，"__blank__" = 棍母模板（空模板，无任何建筑）。</summary>
    [JsonPropertyName("选定模板")] public string SelectedTemplate { get; set; } = "";

    /// <summary>由模板撑开的区域基础边长（0 = 未设置）；实际区域 = max(等级尺寸, 该值)。</summary>
    [JsonPropertyName("区域基础尺寸")] public int AreaBaseSize { get; set; }
}

public static class Prefs
{
    private static readonly Dictionary<string, PlayerPrefs> Map = new();
    private static readonly object Gate = new();
    private static string Path_ = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Init(string path)
    {
        Path_ = path;
        try
        {
            if (!File.Exists(path)) { return; }
            var d = JsonSerializer.Deserialize<Dictionary<string, PlayerPrefs>>(File.ReadAllText(path));
            if (d is null) { return; }
            lock (Gate) { foreach (var kv in d) { Map[kv.Key] = kv.Value; } }
        }
        catch { }
    }

    public static PlayerPrefs Get(string key)
    {
        lock (Gate)
        {
            if (!Map.TryGetValue(key, out var p)) { p = new PlayerPrefs(); Map[key] = p; }
            return p;
        }
    }

    public static void Save()
    {
        try
        {
            lock (Gate) { File.WriteAllText(Path_, JsonSerializer.Serialize(Map, Json)); }
        }
        catch { }
    }
}