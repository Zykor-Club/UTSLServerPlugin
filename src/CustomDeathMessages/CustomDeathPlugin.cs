using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using TrProtocol.NetPackets;
using TShockAPI;
using UnifiedServerProcess;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace CustomDeathMessages;

/// <summary>
/// CustomDeathMessages 自定义死亡消息（UTSL 版）
///
/// 与 TShock 版的差异：
///   - 入口：TerrariaPlugin + [ApiVersion] -> BasePlugin + [PluginMetadata]
///   - 死亡包：GetDataHandlers.KillMe 事件在 UTSL 已被删除，改用
///     NetPacketHandler.Register<PlayerDeathV2>；字段一一对应
///     （e.PlayerId -> args.Packet.PlayerSlot，e.PlayerDeathReason -> args.Packet.Reason）
///   - 播报拦截：ServerApi.Hooks.ServerBroadcast 在 UTSL 无对应事件，改用 OTAPI 的
///     HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage（字段 text/color 与上游 e.Message/e.Color 对应）
///   - 每帧清理：ServerApi.Hooks.GameUpdate -> EventHub.Game.PostUpdate（按 ServerContext 清理）
///   - 数据库：去掉自带的 Microsoft.Data.Sqlite / SQLitePCLRaw 依赖，改用 linq2db（走 TShock.DB，同一个 sqlite 文件）
///   - 多世界：Terraria 静态数组改成 server.Main.xxx；待替换消息按 (ServerContext, 槽位) 分表
///   - 日志：TShock.Log.ConsoleInfo/ConsoleError -> Info/Error
///   - 命令执行者：控制台执行时 args.Player 为 null，回退 args.ExecutorActor
///   - 配置：Newtonsoft.Json -> System.Text.Json，目录改为 config/CustomDeathMessages/
/// </summary>
[PluginMetadata("CustomDeathMessages", "1.0.1", "Eustia、星梦", "自定义死亡消息")]
public class CustomDeathPlugin : BasePlugin
{
    // PlayerDeathReason 字段反射
    // 注意: OTAPI 将原版私有字段改为 public，因此需同时使用 Public 和 NonPublic
    private static readonly BindingFlags FieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo? f_player = typeof(PlayerDeathReason).GetField("_sourcePlayerIndex", FieldFlags);
    private static readonly FieldInfo? f_npc = typeof(PlayerDeathReason).GetField("_sourceNPCIndex", FieldFlags);
    private static readonly FieldInfo? f_proj = typeof(PlayerDeathReason).GetField("_sourceProjectileLocalIndex", FieldFlags);
    private static readonly FieldInfo? f_other = typeof(PlayerDeathReason).GetField("_sourceOtherIndex", FieldFlags);
    private static readonly FieldInfo? f_projType = typeof(PlayerDeathReason).GetField("_sourceProjectileType", FieldFlags);
    private static readonly FieldInfo? f_itemType = typeof(PlayerDeathReason).GetField("_sourceItemType", FieldFlags);
    private static readonly FieldInfo? f_itemPrefix = typeof(PlayerDeathReason).GetField("_sourceItemPrefix", FieldFlags);
    private static readonly FieldInfo? f_customReason = typeof(PlayerDeathReason).GetField("_sourceCustomReason", FieldFlags);

    private Configuration _config = null!;

    /// <summary>待替换的死亡消息，按 (世界, 玩家槽位) 存，多世界下槽位会重复</summary>
    private readonly ConcurrentDictionary<(ServerContext, int), (string Message, Color Color)> _pending = new();

    /// <summary>死亡计数表（走 TShock 的 linq2db 连接，和上游同一张表结构）</summary>
    [Table(Name = "DeathCounts")]
    private sealed class DeathCountRecord
    {
        [Column(DataType = DataType.VarChar, Length = 50), PrimaryKey, NotNull]
        public string Name { get; set; } = "";

        [Column, NotNull]
        public int Count { get; set; }
    }

    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock")
            {
                await initInfo.InitializationTask;
            }
        }

        Configuration.Initialize(configRegistrar.Directory);
        _config = Configuration.Load();

        EnsureDatabase();

        NetPacketHandler.Register<PlayerDeathV2>(OnKillMePacket, HandlerPriority.Normal);
        HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage += OnBroadcast;
        UnifierApi.EventHub.Game.PostUpdate.Register(OnGamePostUpdate, HandlerPriority.Normal);

        Commands.ChatCommands.Add(new Command("customdeathmessages.reload", CdmCommand, "cdm")
        {
            HelpText = "死亡消息插件命令。用法: /cdm reload | /cdm debug | /cdm reset <玩家名>"
        });

        TShock.Log.Info("[CustomDeathMessages] 插件已加载");
    }

    public override ValueTask DisposeAsync(bool isDisposing)
    {
        if (isDisposing)
        {
            HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage -= OnBroadcast;
            UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnGamePostUpdate);

            Assembly asm = Assembly.GetExecutingAssembly();
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate.Method?.DeclaringType?.Assembly == asm);

            _pending.Clear();
        }

        return base.DisposeAsync(isDisposing);
    }

    /// <summary>命令执行者：UTSL 控制台执行时 args.Player 为 null，回退到 ExecutorActor</summary>
    private static TSPlayer ArgPlayer(CommandArgs args) => args.Player ?? args.ExecutorActor;

    private void CdmCommand(CommandArgs args)
    {
        TSPlayer player = ArgPlayer(args);

        if (args.Parameters.Count == 0)
        {
            player.SendInfoMessage("用法:");
            player.SendInfoMessage("/cdm reload - 重载死亡消息配置");
            player.SendInfoMessage("/cdm debug - 调试死亡归因检测");
            return;
        }

        switch (args.Parameters[0].ToLowerInvariant())
        {
            case "reload":
            {
                _config = Configuration.Load();
                player.SendSuccessMessage("[CustomDeathMessages] 配置已重载。");
                TShock.Log.Info($"[CustomDeathMessages] 配置已由 {player.Name} 重载。");
                break;
            }
            case "debug":
            {
                DebugFields(player);
                break;
            }
            case "reset":
            {
                if (args.Parameters.Count < 2)
                {
                    player.SendInfoMessage("用法: /cdm reset <玩家名> - 重置指定玩家的死亡计数");
                    break;
                }
                string targetName = string.Join(" ", args.Parameters.Skip(1));
                if (targetName.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    ResetAllDeathCounts();
                    player.SendSuccessMessage("[CustomDeathMessages] 已重置所有玩家的死亡计数。");
                }
                else
                {
                    ResetDeathCount(targetName);
                    player.SendSuccessMessage($"[CustomDeathMessages] 已重置 {targetName} 的死亡计数。");
                }
                break;
            }
            default:
                player.SendInfoMessage("未知子命令。可用: reload, debug");
                break;
        }
    }

    /// <summary>调试命令：显示 PlayerDeathReason 各字段的反射状态</summary>
    private static void DebugFields(TSPlayer player)
    {
        player.SendInfoMessage("[CustomDeathMessages] 字段反射状态:");
        player.SendInfoMessage($"  f_player (NonPublic|Public) = {(f_player != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage($"  f_npc    (NonPublic|Public) = {(f_npc != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage($"  f_proj   (NonPublic|Public) = {(f_proj != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage($"  f_other  (NonPublic|Public) = {(f_other != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage($"  f_projType (NonPublic|Public) = {(f_projType != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage($"  f_itemType (NonPublic|Public) = {(f_itemType != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage($"  f_customReason (NonPublic|Public) = {(f_customReason != null ? "✓ 找到" : "✗ 未找到")}");
        player.SendInfoMessage(string.Empty);

        // 尝试通过 TryGetCausingEntity 公共 API 检测
        // UTSL/USP 差异：TryGetCausingEntity 与 ByPlayer 都被上下文化了，需要先传 RootContext
        player.SendInfoMessage("[CustomDeathMessages] TryGetCausingEntity 可用性检查（USP 下这些 API 需要 RootContext）:");
        ServerContext? ctx = player.GetCurrentServer() ?? UnifiedServerCoordinator.Servers.FirstOrDefault();
        if (ctx is null)
        {
            player.SendErrorMessage("  当前没有可用的服务器上下文，跳过 API 检查");
            return;
        }

        try
        {
            PlayerDeathReason testReason = PlayerDeathReason.ByNPC(0);
            bool result = testReason.TryGetCausingEntity(ctx, out _);
            player.SendInfoMessage($"  ByNPC(0).TryGetCausingEntity() = {result}");

            testReason = PlayerDeathReason.ByPlayer(ctx, 0);
            result = testReason.TryGetCausingEntity(ctx, out _);
            player.SendInfoMessage($"  ByPlayer(0).TryGetCausingEntity() = {result}");

            testReason = PlayerDeathReason.ByOther(0);
            result = testReason.TryGetCausingEntity(ctx, out _);
            player.SendInfoMessage($"  ByOther(0).TryGetCausingEntity() = {result}");

            player.SendSuccessMessage("[CustomDeathMessages] TryGetCausingEntity API 可用 ✓");
        }
        catch (Exception ex)
        {
            player.SendErrorMessage($"[CustomDeathMessages] TryGetCausingEntity 异常: {ex.Message}");
        }
    }

    /// <summary>每帧清理待替换消息（对应上游 ServerApi.Hooks.GameUpdate），只清当前世界</summary>
    private void OnGamePostUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        ServerContext server = args.Content.Server;
        foreach ((ServerContext, int) key in _pending.Keys)
        {
            if (ReferenceEquals(key.Item1, server))
            {
                _pending.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// 玩家死亡包（对应上游的 GetDataHandlers.KillMe 事件）。
    /// UTSL 的 TShockAPI 内部就是这么读这个包的。
    /// </summary>
    private void OnKillMePacket(ref ReceivePacketEvent<PlayerDeathV2> args)
    {
        int playerId = args.Packet.PlayerSlot;
        ServerContext server = args.LocalReceiver.Server;

        if (playerId < 0 || playerId >= TShock.Players.Length) return;
        TSPlayer? victim = TShock.Players[playerId];
        if (victim == null) return;

        // 死亡次数里程碑公告
        HandleDeathMilestone(server, victim.Name);

        (string? msg, Color color) = Build(server, args.Packet.Reason, victim.Name, playerId);
        if (msg != null)
            _pending[(server, playerId)] = (msg, color);
    }

    /// <summary>
    /// 拦截服务端聊天广播（对应上游 ServerApi.Hooks.ServerBroadcast）。
    /// UTSL 里走 OTAPI 的 HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage。
    /// </summary>
    private void OnBroadcast(Terraria.Chat.ChatHelperSystemContext sender, HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessageEventArgs args)
    {
        ServerContext? server = sender?.root?.ToServer();
        string text = args.text.ToString();

        foreach (KeyValuePair<(ServerContext, int), (string Message, Color Color)> kv in _pending)
        {
            if (server != null && !ReferenceEquals(kv.Key.Item1, server)) continue;

            TSPlayer? player = TShock.Players[kv.Key.Item2];
            if (player != null && text.Contains(player.Name, StringComparison.Ordinal))
            {
                args.text = NetworkText.FromLiteral(kv.Value.Message);
                args.color = kv.Value.Color;
                _pending.TryRemove(kv.Key, out _);
                return;
            }
        }
    }

    /// <summary>处理死亡次数里程碑公告</summary>
    private void HandleDeathMilestone(ServerContext server, string playerName)
    {
        if (_config.DeathMilestones.Count == 0)
            return;

        int newCount = IncrementDeathCount(playerName);

        if (_config.DeathMilestones.TryGetValue(newCount, out string? milestoneMsg))
        {
            string msg = milestoneMsg
                .Replace("{Player}", playerName, StringComparison.OrdinalIgnoreCase)
                .Replace("{Count}", newCount.ToString(), StringComparison.OrdinalIgnoreCase);
            TShockAPI.Utils.Broadcast(server, msg, Color.LightBlue);
        }
    }

    private static readonly Color DefaultMessageColor = new(225, 100, 100);

    private static Color ParseColor(string rgb, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rgb))
                return fallback;

            string[] parts = rgb.Split(',');
            if (parts.Length == 3)
            {
                return new Color(
                    int.Parse(parts[0].Trim()),
                    int.Parse(parts[1].Trim()),
                    int.Parse(parts[2].Trim())
                );
            }
            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private (string? Message, Color Color) Build(ServerContext server, PlayerDeathReason r, string playerName, int playerId = -1)
    {
        int plr = GetIntField(f_player, r);
        int npc = GetIntField(f_npc, r);
        int proj = GetIntField(f_proj, r);
        int other = GetIntField(f_other, r);
        int projType = GetIntField(f_projType, r);
        int itemType = GetIntField(f_itemType, r);
        string? customReason = GetStringField(f_customReason, r);

        // 构建基础占位符
        Dictionary<string, string> ph = new(StringComparer.OrdinalIgnoreCase)
        {
            ["{Player}"] = playerName,
            ["{Killer}"] = "???",
            ["{NPC}"] = "???",
            ["{Projectile}"] = "???",
            ["{Item}"] = "???",
            ["{CustomReason}"] = "",
            ["{Buff}"] = "",
        };

        string category;

        // 1. 自定义理由（其他插件通过 ByCustomReason 设置，优先级最高）
        if (!string.IsNullOrEmpty(customReason))
        {
            ph["{CustomReason}"] = customReason;
            category = "自定义";
        }
        // 2. PvP 击杀
        else if (plr >= 0 && plr < Main.maxPlayers)
        {
            ph["{Killer}"] = GetPlayerName(server, plr);
            ph["{Item}"] = GetItemName(itemType);

            if (proj >= 0)
            {
                ph["{Projectile}"] = GetProjectileName(projType);
                category = "PVP弹幕击杀";
            }
            else
            {
                category = "PVP击杀";
            }
        }
        // 3. NPC / Boss 击杀
        else if (npc >= 0 && npc < Main.maxNPCs)
        {
            NPC n = server.Main.npc[npc];
            ph["{NPC}"] = n.active ? n.GivenOrTypeName : "未知怪物";
            ph["{Killer}"] = ph["{NPC}"];
            category = "NPC击杀";
        }
        // 4. 环境弹幕击杀（无玩家所属）
        else if (proj >= 0)
        {
            ph["{Projectile}"] = GetProjectileName(projType);
            category = "弹幕击杀";
        }
        // 5. 环境死亡（_sourceOtherIndex）
        else if (other >= 0)
        {
            category = other switch
            {
                0 => "摔死",
                1 => "溺水",
                2 => "岩浆",
                3 => "普通",
                4 or 255 => "击杀",
                5 => "石化",
                6 => "刺穿",
                7 => "窒息",
                8 => "烧死",
                9 => "中毒",
                10 => "触电",
                11 => "逃离血肉墙",
                12 => "被舔",
                >= 13 and <= 15 => "传送",
                16 => "炼狱之火",
                17 => "黑暗吞噬",
                18 => "饥饿",
                19 => "太空",
                20 => "挡刀",
                21 => "深渊",
                22 => "吸血鬼自燃",
                _ => "未知",
            };
        }
        // 6. 兜底：所有反射字段均为 -1 时，尝试通过公共 TryGetCausingEntity API 检测
        else
        {
            if (r.TryGetCausingEntity(server, out Entity entity))
            {
                string fallbackCategory = entity switch
                {
                    Player => "PVP击杀",
                    NPC => "NPC击杀",
                    Projectile => "弹幕击杀",
                    _ => "未知",
                };
                category = fallbackCategory;

                if (entity != null)
                {
                    if (entity is Player p)
                    {
                        ph["{Killer}"] = p.name;
                    }
                    else if (entity is NPC nEnt)
                    {
                        ph["{NPC}"] = nEnt.GivenOrTypeName;
                        ph["{Killer}"] = ph["{NPC}"];
                    }
                    else if (entity is Projectile projEntity)
                    {
                        ph["{Projectile}"] = Lang.GetProjectileName(projEntity.type).Value;
                    }
                }
            }
            else
            {
                category = "未知";
            }
        }

        // Buff 细化检测：在环境死亡时检测玩家身上与死亡原因相关的具体 Debuff
        if (playerId >= 0)
        {
            string? buffName = GetDeathBuffName(server, playerId, other);
            if (buffName != null)
                ph["{Buff}"] = buffName;
        }

        _config.Messages.TryGetValue(category, out Configuration.DeathCategoryConfig? catConfig);
        string? msg = FormatMessage(catConfig, ph);
        Color color = ParseColor(catConfig?.Color ?? "", DefaultMessageColor);
        return (msg, color);
    }

    private static string? GetDeathBuffName(ServerContext server, int playerId, int otherIndex)
    {
        Player p = server.Main.player[playerId];
        if (p == null || !p.active)
            return null;

        static bool HasBuff(Player player, int buffId)
        {
            for (int i = 0; i < player.buffType.Length; i++)
            {
                if (player.buffTime[i] > 0 && player.buffType[i] == buffId)
                    return true;
            }
            return false;
        }

        static string? CheckBuffs(Player player, int[] buffIds, string[] names)
        {
            for (int i = 0; i < buffIds.Length; i++)
            {
                if (HasBuff(player, buffIds[i]))
                    return names[i];
            }
            return null;
        }

        return otherIndex switch
        {
            8 => CheckBuffs(p,
            [
                BuffID.OnFire, BuffID.CursedInferno, BuffID.Frostburn,
                BuffID.Burning, BuffID.OnFire3, BuffID.Frostburn2,
                BuffID.ShadowFlame, BuffID.Oiled,
            ],
            [
                "身上着火了", "被诅咒地狱火缠绕", "被冰焰灼烧",
                "被陨石灼伤", "被神圣之火吞噬", "被极寒冰焰吞噬",
                "被暗影焰缠身", "身上沾满了油",
            ]),

            9 => CheckBuffs(p,
            [
                BuffID.Poisoned, BuffID.Venom,
            ],
            [
                "中毒了", "被毒液侵蚀",
            ]),

            10 => HasBuff(p, BuffID.Electrified) ? "触电了" : null,

            5 => HasBuff(p, BuffID.Stoned) ? "石化了" : null,

            7 => CheckBuffs(p,
            [
                BuffID.Suffocation, BuffID.WindPushed,
            ],
            [
                "被掩埋窒息", "被大风吹飞",
            ]),

            16 => CheckBuffs(p,
            [
                BuffID.Burning, BuffID.OnFire3,
            ],
            [
                "被陨石灼烧", "被炼狱火焰吞噬",
            ]),

            18 => HasBuff(p, BuffID.Starving) ? "饿晕了" : null,

            _ => null,
        };
    }

    // ---------- 数据库（linq2db，替代上游自带的 Microsoft.Data.Sqlite） ----------

    /// <summary>借 TShock 已建立的连接参数新建连接（linq2db 的 DataConnection 非线程安全）</summary>
    private static DataConnection OpenDb() => new(TShock.DB.Options);

    private static void EnsureDatabase()
    {
        using DataConnection db = OpenDb();
        db.CreateTable<DeathCountRecord>(tableOptions: TableOptions.CreateIfNotExists);
        TShock.Log.Info("[CustomDeathMessages] 死亡计数表已就绪（走 TShock 的 linq2db 连接）");
    }

    private static int IncrementDeathCount(string playerName)
    {
        using DataConnection db = OpenDb();
        ITable<DeathCountRecord> table = db.GetTable<DeathCountRecord>();

        int affected = table
            .Where(r => r.Name == playerName)
            .Set(r => r.Count, r => r.Count + 1)
            .Update();

        if (affected == 0)
        {
            try
            {
                table.Insert(() => new DeathCountRecord { Name = playerName, Count = 1 });
            }
            catch
            {
                // 并发下可能已被别的线程插入，忽略
            }
        }

        return table.Where(r => r.Name == playerName).Select(r => r.Count).FirstOrDefault();
    }

    private static void ResetDeathCount(string playerName)
    {
        using DataConnection db = OpenDb();
        db.GetTable<DeathCountRecord>().Where(r => r.Name == playerName).Delete();
    }

    private static void ResetAllDeathCounts()
    {
        using DataConnection db = OpenDb();
        db.GetTable<DeathCountRecord>().Delete();
    }

    // ---------- 辅助 ----------

    private static string? FormatMessage(Configuration.DeathCategoryConfig? catConfig, Dictionary<string, string> placeholders)
    {
        if (catConfig == null || catConfig.Messages.Count == 0)
            return null;

        string template = catConfig.Messages[Random.Shared.Next(catConfig.Messages.Count)];

        foreach ((string key, string value) in placeholders)
            template = template.Replace(key, value, StringComparison.OrdinalIgnoreCase);

        return template;
    }

    private static int GetIntField(FieldInfo? f, PlayerDeathReason obj)
        => f != null ? (int)(f.GetValue(obj) ?? -1) : -1;

    private static string? GetStringField(FieldInfo? f, PlayerDeathReason obj)
        => f?.GetValue(obj) as string;

    private static string GetPlayerName(ServerContext server, int index)
    {
        if (index >= 0 && index < server.Main.player.Length)
        {
            Player p = server.Main.player[index];
            if (p != null && p.active)
                return p.name;
        }
        TSPlayer? tsPlayer = TShock.Players[index];
        return tsPlayer?.Name ?? "未知玩家";
    }

    private static string GetItemName(int type)
    {
        if (type > 0 && type < ItemID.Count)
        {
            string name = Lang.GetItemNameValue(type);
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return "未知武器";
    }

    private static string GetProjectileName(int type)
    {
        if (type > 0 && type < ProjectileID.Count)
        {
            string name = Lang.GetProjectileName(type).Value;
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return "未知弹幕";
    }
}
