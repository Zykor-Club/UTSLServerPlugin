using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Timers;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using TrProtocol.NetPackets;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace BetterBack;

/// <summary>
/// BetterBack 增强版 Back（UTSL 版）
///
/// 与 TShock 版的差异：
///   - 入口：TerrariaPlugin + [ApiVersion] -> BasePlugin + [PluginMetadata]
///   - 死亡：GetDataHandlers.KillMe（UTSL 已删除）-> NetPacketHandler.Register<PlayerDeathV2>
///   - 离开：ServerApi.Hooks.ServerLeave -> UnifierApi.EventHub.Netplay.LeaveEvent
///   - 进服：ServerApi.Hooks.NetGreetPlayer -> TShockAPI.Hooks.PlayerHooks.PlayerPostLogin
///   - 世界进度：NPC.downedBoss3 / downedPlantBoss -> server.NPC.downedBoss3 / downedPlantBoss（按世界）
///   - NPC 数组：Main.npc -> server.Main.npc
///   - 配置：Newtonsoft.Json -> System.Text.Json，tshock/ -> config/BetterBack/
///   - 命令执行者：控制台执行时 args.Player 为 null，回退 args.ExecutorActor
/// </summary>
[PluginMetadata("BetterBack", "2026.5.2.0", "星梦XM", "增强版Back")]
public sealed class BetterBackPlugin : BasePlugin
{
    public const string PermissionUse = "betterback.use";
    public const string PermissionBuff = "betterback.buff";
    public const string PermissionGod = "betterback.god";
    public const string PermissionAdmin = "betterback.admin";

    private readonly DataManager _dataManager = new();
    private readonly ConcurrentDictionary<int, DateTime> _cooldowns = new();
    private readonly ConcurrentDictionary<int, DateTime> _godModePlayers = new();
    private readonly ConcurrentDictionary<int, DateTime> _autoReturnTimers = new();
    private readonly ConcurrentDictionary<int, bool> _wasDead = new();
    // 显式写全名：.NET 9 的隐式 global using 让 Timer 在 System.Timers 与 System.Threading 之间二义
    private System.Timers.Timer? _updateTimer;
    private readonly List<Command> _registeredCommands = new();
    private CommandHandler? _commandHandler;

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

        BetterBackConfig.Initialize(configRegistrar.Directory);
        BetterBackConfig.Instance.Load();

        // 替代 GetDataHandlers.KillMe（见类注释）
        NetPacketHandler.Register<PlayerDeathV2>(OnPlayerDeath, HandlerPriority.Normal);
        UnifierApi.EventHub.Netplay.LeaveEvent.Register(OnPlayerLeave, HandlerPriority.Normal);
        PlayerHooks.PlayerPostLogin += OnPlayerGreet;
        GeneralHooks.ReloadEvent += OnReload;

        _updateTimer = new System.Timers.Timer(1000);
        _updateTimer.Elapsed += OnTimerElapsed;
        _updateTimer.AutoReset = true;
        _updateTimer.Start();

        _commandHandler = new CommandHandler(_dataManager, _cooldowns, _godModePlayers, _autoReturnTimers);
        RegisterCommands();
        TShock.Log.Info("[BetterBack] 插件已加载 v2026.5.2.0");
    }

    public override ValueTask DisposeAsync(bool isDisposing)
    {
        if (isDisposing)
        {
            UnifierApi.EventHub.Netplay.LeaveEvent.UnRegister(OnPlayerLeave);
            PlayerHooks.PlayerPostLogin -= OnPlayerGreet;
            GeneralHooks.ReloadEvent -= OnReload;

            _updateTimer?.Stop();
            _updateTimer?.Dispose();

            foreach (Command cmd in _registeredCommands)
                Commands.ChatCommands.Remove(cmd);
            _registeredCommands.Clear();
        }

        return base.DisposeAsync(isDisposing);
    }

    private void RegisterCommands()
    {
        RegisterCommand(new Command(PermissionUse, args => _commandHandler?.HandleBetCommand(args), "bet")
        { HelpText = "/bet [序号] - 传送至死亡点(无序号则传最新)，/bet list - 列表，/bet clear - 清除，/bet auto - 自动返回" });

        RegisterCommand(new Command(PermissionBuff, args => _commandHandler?.HandleBuffCommand(args), "betbuff")
        { HelpText = "管理传送Buff: add <id>, remove <id>, list" });

        RegisterCommand(new Command(PermissionGod, args => _commandHandler?.HandleGodCommand(args), "betgod")
        { HelpText = "管理无敌时间: time <秒数>, info" });
    }

    private void RegisterCommand(Command cmd)
    {
        _registeredCommands.Add(cmd);
        Commands.ChatCommands.Add(cmd);
    }

    private void OnReload(ReloadEventArgs args)
    {
        BetterBackConfig.Instance.Load();
        args.Player?.SendSuccessMessage("[BetterBack] 配置已重载");
        TShock.Log.Info("[BetterBack] 配置已通过 /reload 重载");
    }

    #region 事件处理

    /// <summary>对应上游 GetDataHandlers.KillMe。字段取法与 UTSL 的 TShockAPI 内部一致。</summary>
    private void OnPlayerDeath(ref ReceivePacketEvent<PlayerDeathV2> args)
    {
        try
        {
            int slot = args.Packet.PlayerSlot;
            ServerContext server = args.LocalReceiver.Server;

            if (slot < 0 || slot >= TShock.Players.Length)
                return;

            TSPlayer? player = TShock.Players[slot];
            if (player == null)
                return;

            if (!player.HasPermission(PermissionUse))
                return;

            if (BetterBackConfig.Instance.BlockDungeonDeathBeforeSkeletron &&
                player.TPlayer.ZoneDungeon && !server.NPC.downedBoss3)
            {
                player.SendInfoMessage("[BetterBack] 未击败骷髅王，地牢死亡点未被记录。");
                return;
            }

            if (BetterBackConfig.Instance.BlockTempleDeathBeforePlantera &&
                player.TPlayer.ZoneLihzhardTemple && !server.NPC.downedPlantBoss)
            {
                player.SendInfoMessage("[BetterBack] 未击败世纪之花，神庙死亡点未被记录。");
                return;
            }

            string reason = ExtractDeathReason(server, args.Packet.Reason);
            string playerName = player.Account?.Name ?? player.Name;

            if (_dataManager.AddDeathPoint(playerName, (int)player.X, (int)player.Y, reason))
            {
                int count = _dataManager.GetPlayerDeathCount(playerName);
                int max = BetterBackConfig.Instance.MaxDeathPointsPerPlayer;
                player.SendInfoMessage(string.Format(BetterBackConfig.Instance.DeathPointRecordedMessage, count, max));
            }

            _wasDead[player.Index] = true;
            _autoReturnTimers.TryRemove(player.Index, out _);
        }
        catch (Exception ex)
        {
            TShock.Log.Error($"[BetterBack] 记录死亡点失败: {ex}");
        }
    }

    /// <summary>对应上游 ServerApi.Hooks.ServerLeave</summary>
    private void OnPlayerLeave(ref ReadonlyNoCancelEventArgs<LeaveEvent> args)
    {
        TSPlayer? player = TShock.Players[args.Content.Who];
        if (player is { IsLoggedIn: true })
        {
            _godModePlayers.TryRemove(player.Index, out _);
            _cooldowns.TryRemove(player.Index, out _);
            _autoReturnTimers.TryRemove(player.Index, out _);
            _wasDead.TryRemove(player.Index, out _);
        }
    }

    /// <summary>对应上游 ServerApi.Hooks.NetGreetPlayer（UTSL 没有 NetGreetPlayer，用登录完成代替）</summary>
    private void OnPlayerGreet(PlayerPostLoginEventArgs e)
    {
        TSPlayer? player = e.Player;
        if (player is not { IsLoggedIn: true })
            return;

        int count = _dataManager.GetPlayerDeathCount(player.Account?.Name ?? player.Name);
        if (count > 0)
        {
            player.SendInfoMessage($"[BetterBack] 您有 {count} 个历史死亡点，使用 /bet 传送至最新，或 /bet list 查看列表。");
        }
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        DateTime now = DateTime.Now;

        foreach ((int index, DateTime endTime) in _godModePlayers.ToList())
        {
            TimeSpan remaining = endTime - now;
            if (remaining.TotalSeconds <= 0)
            {
                if (_godModePlayers.TryRemove(index, out _))
                {
                    TSPlayer? player = TShock.Players[index];
                    if (player is { Active: true })
                    {
                        player.GodMode = false;
                        player.SendInfoMessage("[BetterBack] 无敌时间已结束。");
                    }
                }
            }
            else if (remaining.TotalSeconds <= 3)
            {
                TSPlayer? player = TShock.Players[index];
                player?.SendInfoMessage($"[BetterBack] 无敌时间剩余: {remaining.TotalSeconds:F0}秒");
            }
        }

        foreach (TSPlayer? player in TShock.Players)
        {
            if (player?.Active != true || !player.IsLoggedIn) continue;

            bool isDead = player.Dead || player.TPlayer.dead;
            bool wasDead = _wasDead.GetValueOrDefault(player.Index);

            if (wasDead && !isDead)
            {
                int delay = _dataManager.GetAutoReturnDelay(player.Account?.Name ?? player.Name);
                if (delay > 0)
                    _autoReturnTimers[player.Index] = now.AddSeconds(delay);
            }
            _wasDead[player.Index] = isDead;
        }

        foreach ((int index, DateTime endTime) in _autoReturnTimers.ToList())
        {
            TSPlayer? player = TShock.Players[index];
            if (player?.Active != true)
            {
                _autoReturnTimers.TryRemove(index, out _);
                continue;
            }

            TimeSpan remaining = endTime - now;
            if (remaining.TotalSeconds <= 0)
            {
                if (_autoReturnTimers.TryRemove(index, out _))
                {
                    player.SendInfoMessage("[BetterBack] 自动返回倒计时结束，正在传送...");
                    _commandHandler?.TeleportToDeathPoint(player, 0);
                }
            }
            else if (remaining.TotalSeconds <= 5)
            {
                player.SendInfoMessage($"[BetterBack] 自动返回剩余: {remaining.TotalSeconds:F0}秒");
            }
        }
    }

    #endregion

    #region 工具方法

    /// <summary>UTSL: NPC 数组要取所属世界的 server.Main.npc</summary>
    private static string ExtractDeathReason(ServerContext server, PlayerDeathReason? reason)
    {
        if (reason == null) return "未知原因";

        int npcIndex = reason._sourceNPCIndex;
        NPC[] npcs = server.Main.npc;
        if (npcIndex >= 0 && npcIndex < npcs.Length && npcs[npcIndex].active)
            return $"被 {npcs[npcIndex].FullName} 击杀";

        if (reason._sourceProjectileType > 0)
            return "被弹幕击杀";

        int attackerIndex = reason._sourcePlayerIndex;
        if (attackerIndex >= 0 && attackerIndex < TShock.Players.Length)
        {
            TSPlayer? attacker = TShock.Players[attackerIndex];
            if (attacker != null) return $"被 {attacker.Name} 击杀";
        }

        return reason._sourceCustomReason ?? "未知原因";
    }

    #endregion
}
