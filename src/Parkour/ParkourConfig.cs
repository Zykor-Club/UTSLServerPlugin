using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parkour;

/// <summary>跑酷插件配置（config/Parkour/settings.json）。</summary>
public sealed class ParkourConfig
{
    /// <summary>排行榜赛季标识（与 MiniGameCore 的赛季保持一致最省事）。</summary>
    [JsonPropertyName("赛季")] public string Season { get; set; } = "s1";


    /// <summary>是否给玩家显示区域边界（照 RegionView：只对自己可见，默认开）。</summary>
    [JsonPropertyName("区域显示")] public bool RegionDisplay { get; set; } = true;

    /// <summary>区域显示刷新间隔（秒）。</summary>
    [JsonPropertyName("区域显示_刷新秒")] public int RegionDisplayRefreshSeconds { get; set; } = 2;

    /// <summary>区域显示的可视范围（格，以玩家为中心）。</summary>
    [JsonPropertyName("区域显示_范围格")] public int RegionDisplayRange { get; set; } = 96;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ParkourConfig Load(string path)
    {
        ParkourConfig config = new();
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                config = JsonSerializer.Deserialize<ParkourConfig>(File.ReadAllText(path), JsonOptions) ?? new ParkourConfig();
            }
        }
        catch (Exception ex)
        {
            // ★ 解析失败不要覆盖用户文件：直接抛上去让调用方决定（保留原文件）
            TShockAPI.TShock.Log.Error("[跑酷] 配置解析失败，使用默认值: " + ex.Message);
            config = new ParkourConfig();
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
