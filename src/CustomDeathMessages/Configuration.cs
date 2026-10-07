using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomDeathMessages;

public class Configuration
{
    private static string _configDirectory = "";

    /// <summary>配置文件完整路径（由 Initialize 指定目录）</summary>
    public static string ConfigPath => Path.Combine(_configDirectory, "CustomDeathMessages.json");

    private static readonly JsonSerializerOptions JsonSettings = new()
    {
        WriteIndented = true,
        // 中文原样输出，不转义成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 由插件初始化时调用，指定配置目录。
    /// UTSL 下传入 IPluginConfigRegistrar.Directory，即 config/CustomDeathMessages/
    /// </summary>
    public static void Initialize(string directory)
    {
        _configDirectory = directory;
        Directory.CreateDirectory(directory);
    }

    [JsonPropertyName("死亡消息模板")]
    public Dictionary<string, DeathCategoryConfig> Messages { get; set; } = new()
    {
        ["摔死"] = new() { Messages = new() { "[i:321]{Player} 摔成了一滩。", "[i:321]{Player} 从高处自由落体。", "[i:321]{Player} 低估了重力。" } },
        ["溺水"] = new() { Messages = new() { "[i:321]{Player} 溺水了。", "[i:321]{Player} 在水里失去了意识。" } },
        ["岩浆"] = new() { Messages = new() { "[i:321]{Player} 试图在岩浆里游泳。", "[i:321]{Player} 在岩浆中融化了。" } },
        ["普通"] = new() { Messages = new() { "[i:321]{Player} 被干掉了。", "[i:321]{Player} 死了。" } },
        ["击杀"] = new() { Messages = new() { "[i:321]{Player} 被击杀了。", "[i:321]{Player} 倒下了。" } },
        ["石化"] = new() { Messages = new() { "[i:321]{Player} 石化后摔碎了。", "[i:321]{Player} 变成了碎石。" } },
        ["刺穿"] = new() { Messages = new() { "[i:321]{Player} 被刺穿了。", "[i:321]{Player} 被戳死了。" } },
        ["窒息"] = new() { Messages = new() { "[i:321]{Player} 窒息了。", "[i:321]{Player} 喘不过气来。" } },
        ["烧死"] = new() { Messages = new() { "[i:321]{Player} 被烧死了。", "[i:321]{Player} 在火焰中化为灰烬。" } },
        ["中毒"] = new() { Messages = new() { "[i:321]{Player} 中毒身亡。", "[i:321]{Player} 被毒死了。" } },
        ["触电"] = new() { Messages = new() { "[i:321]{Player} 触电了。", "[i:321]{Player} 被电焦了。" } },
        ["逃离血肉墙"] = new() { Messages = new() { "[i:321]{Player} 试图逃离血肉墙。", "[i:321]{Player} 没能逃出地狱。" } },
        ["被舔"] = new() { Messages = new() { "[i:321]{Player} 被舔了一口。", "[i:321]{Player} 被黏糊糊的舌头带走了。" } },
        ["传送"] = new() { Messages = new() { "[i:321]{Player} 传送事故。", "[i:321]{Player} 在传送中迷失了。" } },
        ["炼狱之火"] = new() { Messages = new() { "[i:321]{Player} 在炼狱之火中烧成灰。", "[i:321]{Player} 被地狱烈焰吞噬了。" } },
        ["黑暗吞噬"] = new() { Messages = new() { "[i:321]{Player} 在黑暗中被吞噬了。", "[i:321]{Player} 消失在黑暗中。" } },
        ["饥饿"] = new() { Messages = new() { "[i:321]{Player} 饿死了。", "[i:321]{Player} 因饥饿而倒下。" } },
        ["太空"] = new() { Messages = new() { "[i:321]{Player} 飞到了外太空。", "[i:321]{Player} 脱离了大气层。" } },
        ["挡刀"] = new() { Messages = new() { "[i:321]{Player} 替队友挡了致命一击。", "[i:321]{Player} 为队友牺牲了。" } },
        ["深渊"] = new() { Messages = new() { "[i:321]{Player} 掉到了世界底部。", "[i:321]{Player} 坠入了深渊。" } },
        ["吸血鬼自燃"] = new() { Messages = new() { "[i:321]{Player} 在阳光下自燃了。", "[i:321]{Player} 被阳光烧成了灰。" } },

        ["PVP击杀"] = new() { Messages = new() { "[i:757]{Player} 被 {Killer} 干掉了！", "[i:757]{Player} 被 {Killer} 击杀了。", "[i:757]{Player} 被 {Killer} 打得落花流水。" } },
        ["PVP弹幕击杀"] = new() { Messages = new() { "[i:757]{Player} 被 {Killer} 用 {Projectile} 射杀了！", "[i:757]{Player} 被 {Killer} 的 {Projectile} 击倒了。" } },
        ["NPC击杀"] = new() { Color = "205,133,63", Messages = new() { "[i:320]{Player} 被 {NPC} 杀死了。", "[i:320]{Player} 被 {NPC} 击败了。", "[i:320]{Player} 成了 {NPC} 的盘中餐。" } },
        ["弹幕击杀"] = new() { Messages = new() { "{Player} 被弹幕击中身亡。", "{Player} 被 {Projectile} 击中了。" } },
        ["自定义"] = new() { Messages = new() { "[i:321]{Player} {CustomReason}" } },

        ["未知"] = new() { Messages = new() { "[i:321]{Player} 死了。", "[i:321]{Player} 倒下了。" } },
    };

    [JsonPropertyName("死亡次数公告")]
    public Dictionary<int, string> DeathMilestones { get; set; } = new()
    {
        [10] = "公告：{Player} 已经死亡 10 次了！",
        [50] = "公告：{Player} 已经死亡 50 次了。",
        [100] = "公告：{Player} 已经死亡 100 次！",
        [500] = "公告：{Player} 已经死亡 500 次，真是坚持不懈！",
        [1000] = "公告：{Player} 已经死亡 1000 次！",
    };

    public class DeathCategoryConfig
    {
        // 注意：原版这里的 JSON 键漏了一个右括号，为保证老配置文件兼容，保持原样
        [JsonPropertyName("颜色（RGB值")]
        public string Color { get; set; } = "";

        [JsonPropertyName("消息")]
        public List<string> Messages { get; set; } = new();
    }

    public static Configuration Load()
    {
        if (!File.Exists(ConfigPath))
        {
            Configuration config = new();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonSettings));
            TShockAPI.TShock.Log.Info("[CustomDeathMessages] 已创建默认配置文件: " + ConfigPath);
            return config;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Configuration>(json, JsonSettings) ?? new Configuration();
        }
        catch (Exception ex)
        {
            TShockAPI.TShock.Log.Error("[CustomDeathMessages] 配置文件加载失败，使用默认配置: " + ex.Message);
            return new Configuration();
        }
    }
}
