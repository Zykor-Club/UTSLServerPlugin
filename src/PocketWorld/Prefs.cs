using System.Text.Json;
using System.Text.Json.Serialization;

namespace PocketWorld;

/// <summary>玩家的个人开关（保持联系 / 同步状态），按玩家标识持久化到 players.json。</summary>
public sealed class PlayerPrefs
{
    [JsonPropertyName("保持联系")] public bool Contact { get; set; }
    [JsonPropertyName("同步状态")] public bool Sync { get; set; }

    /// <summary>家园活动区域的等级（每级 +配置增量 半径）。</summary>
    [JsonPropertyName("活动区域等级")] public int AreaLevel { get; set; }
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