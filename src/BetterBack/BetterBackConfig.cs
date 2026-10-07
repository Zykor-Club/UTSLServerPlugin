using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterBack;

public sealed class BetterBackConfig
{
    private static readonly Lazy<BetterBackConfig> _instance = new(() => new());
    private static string _configDirectory = "";

    /// <summary>UTSL: config/BetterBack/BetterBack.json（上游是 tshock/BetterBack.json）</summary>
    public static string ConfigPath => Path.Combine(_configDirectory, "BetterBack.json");

    private static readonly JsonSerializerOptions JsonSettings = new()
    {
        WriteIndented = true,
        // 中文原样输出，不转义
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 对应上游的 NullValueHandling.Ignore
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BetterBackConfig Instance => _instance.Value;

    /// <summary>由 InitializeAsync 调用（IPluginConfigRegistrar.Directory）</summary>
    public static void Initialize(string directory)
    {
        _configDirectory = directory;
        Directory.CreateDirectory(directory);
    }

    [JsonPropertyName("最大死亡点数量")] public int MaxDeathPointsPerPlayer { get; set; } = 5;
    [JsonPropertyName("传送冷却时间")] public int TeleportCooldown { get; set; } = 30;

    [JsonPropertyName("默认传送BuffID")] public List<int> DefaultBuffIDs { get; set; } = new() { 1, 3, 5 };
    [JsonPropertyName("Buff持续时间")] public int BuffDuration { get; set; } = 10;
    [JsonPropertyName("无敌时间")] public int GodModeDuration { get; set; } = 5;

    [JsonPropertyName("死亡点记录消息")] public string DeathPointRecordedMessage { get; set; } = "已记录死亡点 ({0}/{1})";
    [JsonPropertyName("传送成功消息")] public string TeleportSuccessMessage { get; set; } = "已传送至死亡点: {0}";
    [JsonPropertyName("无敌时间消息")] public string GodModeMessage { get; set; } = "您获得了 {0} 秒无敌时间";
    [JsonPropertyName("冷却时间消息")] public string CooldownMessage { get; set; } = "请等待 {0:F1} 秒后再试";

    [JsonPropertyName("禁止记录未击败骷髅王的地牢死亡")] public bool BlockDungeonDeathBeforeSkeletron { get; set; } = true;
    [JsonPropertyName("禁止记录未击败世纪之花的神庙死亡")] public bool BlockTempleDeathBeforePlantera { get; set; } = true;

    // ⚠️ 必须是 public：System.Text.Json 反序列化要求公开无参构造函数
    //（Newtonsoft 允许 private，所以上游是 private 的 —— 这个差异会让 Load() 直接抛
    //  NotSupportedException，而且只在配置文件已存在时才暴露）
    public BetterBackConfig() { }

    public void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(_configDirectory))
                return;

            if (!File.Exists(ConfigPath))
            {
                Save();
                return;
            }

            // STJ 没有 Newtonsoft 的 PopulateObject（就地填充已有实例），
            // 所以反序列化出新实例再逐字段拷回来 —— 保留"配置里缺的键用默认值"的语义。
            BetterBackConfig? loaded = JsonSerializer.Deserialize<BetterBackConfig>(File.ReadAllText(ConfigPath), JsonSettings);
            if (loaded == null)
                return;

            MaxDeathPointsPerPlayer = loaded.MaxDeathPointsPerPlayer;
            TeleportCooldown = loaded.TeleportCooldown;
            DefaultBuffIDs = loaded.DefaultBuffIDs ?? new() { 1, 3, 5 };
            BuffDuration = loaded.BuffDuration;
            GodModeDuration = loaded.GodModeDuration;
            DeathPointRecordedMessage = loaded.DeathPointRecordedMessage;
            TeleportSuccessMessage = loaded.TeleportSuccessMessage;
            GodModeMessage = loaded.GodModeMessage;
            CooldownMessage = loaded.CooldownMessage;
            BlockDungeonDeathBeforeSkeletron = loaded.BlockDungeonDeathBeforeSkeletron;
            BlockTempleDeathBeforePlantera = loaded.BlockTempleDeathBeforePlantera;
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error($"[BetterBack] 配置加载失败: {ex}");
        }
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(_configDirectory))
                return;

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonSettings));
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error($"[BetterBack] 配置保存失败: {ex}");
        }
    }
}
