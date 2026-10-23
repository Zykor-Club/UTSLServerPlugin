using System.Text.Json;
using System.Text.Json.Serialization;

namespace UTSL.MiniGame;

/// <summary>小游戏核心配置（config/MiniGameCore/settings.json）。</summary>
public sealed class MiniGameCoreConfig
{
    /// <summary>同时在跑的房间上限（0 = 不限）。每个房间是一个整世界，仍然建议给个上限。</summary>
    [JsonPropertyName("房间_最多同时存在")] public int MaxRooms { get; set; } = 8;

    /// <summary>等候厅空置多久后卸载（秒）。0 = 立刻卸载。</summary>
    [JsonPropertyName("等候厅_空置卸载秒")] public int LobbyUnloadSeconds { get; set; } = 5;

    /// <summary>多人等待超时（秒）：到点还没满员就按现有人数开局。0 = 一直等。</summary>
    [JsonPropertyName("等候厅_就绪超时秒")] public int LobbyReadyTimeoutSeconds { get; set; } = 120;

    /// <summary>房间空置多久后销毁（秒）。0 = 常驻。</summary>
    [JsonPropertyName("房间_空置销毁秒")] public int RoomIdleDestroySeconds { get; set; } = 300;

    /// <summary>多人首名完赛后的宽限（秒），可被地图覆盖。</summary>
    [JsonPropertyName("宽限_默认秒")] public int GraceSeconds { get; set; } = 60;

    /// <summary>排行榜赛季标识（清榜时改这个值即可开新赛季）。</summary>
    [JsonPropertyName("排行榜_赛季")] public string Season { get; set; } = "s1";


    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static MiniGameCoreConfig Load(string path)
    {
        MiniGameCoreConfig config = new();
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config = JsonSerializer.Deserialize<MiniGameCoreConfig>(File.ReadAllText(path), JsonOptions) ?? new MiniGameCoreConfig();
            }
        }
        catch (Exception ex)
        {
            // ★ 解析失败不要覆盖用户文件：直接抛上去让调用方决定（保留原文件）
            TShockAPI.TShock.Log.Error("[MiniGameCore] 配置解析失败，使用默认值: " + ex.Message);
            config = new MiniGameCoreConfig();
        }

        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                if (!File.Exists(path)) { File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions)); }
            }
        }
        catch { }

        return config;
    }
}
