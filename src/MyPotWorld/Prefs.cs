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