using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupBlacklistPlugin;

public class BlacklistConfig
{
    private static string _configDirectory = "";

    /// <summary>配置文件完整路径（由 Initialize 指定目录）</summary>
    public static string ConfigPath => Path.Combine(_configDirectory, "GroupBlacklist.json");

    private static readonly JsonSerializerOptions JsonSettings = new()
    {
        WriteIndented = true,
        // 中文原样输出，不转义成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 由插件初始化时调用，指定配置目录。
    /// UTSL 下传入 IPluginConfigRegistrar.Directory，即 config/GroupBlacklist/
    /// </summary>
    public static void Initialize(string directory)
    {
        _configDirectory = directory;
        Directory.CreateDirectory(directory);
    }

    [JsonPropertyName("插件设置")]
    public PluginSettings Settings { get; set; } = new();

    [JsonPropertyName("黑名单组列表")]
    public List<string> BlacklistedGroups { get; set; } = ["poooo", "如悠"];

    [JsonPropertyName("豁免玩家列表")]
    public List<string> ExemptPlayers { get; set; } = [];

    public static BlacklistConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            BlacklistConfig cfg = new();
            cfg.Save();
            TShockAPI.TShock.Log.Info("[组黑名单] 已创建默认配置文件");
            return cfg;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<BlacklistConfig>(json, JsonSettings) ?? new BlacklistConfig();
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error($"[组黑名单] 加载配置失败: {ex.Message}");
            return new BlacklistConfig();
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, JsonSettings);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error($"[组黑名单] 保存配置失败: {ex.Message}");
        }
    }
}

public class PluginSettings
{
    [JsonPropertyName("启用插件")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("拒绝加入提示信息")]
    public string KickMessage { get; set; } = "你的用户组被禁止进入此服务器";

    [JsonPropertyName("踢出提示信息")]
    public string InGameKickMessage { get; set; } = "你所属的用户组已被列入黑名单";

    [JsonPropertyName("检测间隔(秒)")]
    public int CheckInterval { get; set; } = 10;

    [JsonPropertyName("是否踢出在线黑名单玩家")]
    public bool KickOnlineBlacklist { get; set; } = true;

    [JsonPropertyName("是否记录日志")]
    public bool LogActions { get; set; } = true;
}
