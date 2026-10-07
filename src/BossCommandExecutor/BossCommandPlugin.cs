using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Terraria;
using Terraria.GameContent;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace BossCommandExecutor
{
    /// <summary>
    /// BossCommandExecutor（UTSL 版）
    ///
    /// 与 TShock 版的关键差异：
    ///   1) 入口：TerrariaPlugin + [ApiVersion(2,1)] -> BasePlugin + [PluginMetadata]
    ///   2) ServerApi.Hooks.NpcSpawn / NpcKilled 在 UTSL 里**不存在**。
    ///      TSAPI 用的那套老式手写钩子 OTAPI.Hooks.NPC.Spawn / .Killed 被 USP 分支砍掉了
    ///      （实测编译期直接 CS0117：Hooks.NPC 未包含 Spawn/Killed 的定义）。
    ///      替代品是 HookEvents.Terraria.NPC.OnSpawn / checkDead —— 底层其实是同一个方法
    ///      （NPC.NewNPC / NPC.checkDead），而且**额外带了 RootContext**，多世界下反而更准。
    ///      OnSpawn 同时给出 NPC 实例（whoAmI/type）和 ServerContext，正好满足 MarkAsAlive 的需求。
    ///   3) On.Terraria.GameContent.BossDamageTracker.OnBossKilled 的签名**没有被上下文化**，
    ///      和上游一致；但它不给 ServerContext，所以要用 FindServerForNpc 反查。
    ///   4) 多世界：Main.npc / Main.worldName 一律换成 server.Main.*；
    ///      BossTracker 的所有索引都补上 ServerContext 维度。
    ///   5) 日志：TShock.Log.ConsoleInfo/ConsoleError/ConsoleDebug -> Info/Error/Debug
    ///   6) 命令执行：Commands.HandleCommand(TSPlayer.Server, cmd)
    ///              -> Commands.HandleCommand(new CommandExecutor(server, byte.MaxValue), cmd)
    ///   7) 配置：Newtonsoft.Json -> System.Text.Json，目录改为 config/BossCommandExecutor/
    /// </summary>
    [PluginMetadata("BossCommandExecutor", "1.5.0", "星梦XM", "Boss击杀后自动执行预设命令")]
    public class BossCommandPlugin : BasePlugin
    {
        private const string PluginVersion = "1.5.0";

        private readonly BossTracker _bossTracker = new();
        private readonly CommandProcessor _commandProcessor = new();
        private readonly DamageRankBroadcaster _damageRanker = new();
        private readonly FloatingTextService _floatingTextService = new();

        internal static Configuration Config { get; private set; } = new();

        /// <summary>
        /// 同一次 Boss 死亡的去重表。
        ///
        /// UTSL 实测：一只 Boss 死亡会**同时**触发两个钩子 ——
        ///   1) HookEvents.Terraria.NPC.checkDead   （对应上游的 NpcKilled）
        ///   2) On.Terraria.GameContent.BossDamageTracker.OnBossKilled
        /// 上游只对"多体节 Boss"做了互斥（OnNpcKilled 里提前 return），
        /// 单体节 Boss（例如史莱姆王）会两条路各跑一遍命令。
        /// 这里按 (世界, 槽位, 类型) 做一个短窗口认领，保证一次死亡只执行一次。
        /// </summary>
        private readonly ConcurrentDictionary<(ServerContext Server, int WhoAmI, int Type), long> _handledKills = new();

        private const long KillDedupWindowMs = 5_000;

        private bool TryClaimKill(ServerContext server, NPC npc)
        {
            long now = Environment.TickCount64;

            foreach (KeyValuePair<(ServerContext, int, int), long> kv in _handledKills)
            {
                if (now - kv.Value > KillDedupWindowMs)
                    _handledKills.TryRemove(kv.Key, out _);
            }

            return _handledKills.TryAdd((server, npc.whoAmI, npc.type), now);
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
            LoadConfig();
            GeneralHooks.ReloadEvent += OnConfigReload;

            // 替代 ServerApi.Hooks.NpcSpawn / NpcKilled（见类注释）
            HookEvents.Terraria.NPC.OnSpawn += OnNpcSpawn;
            HookEvents.Terraria.NPC.checkDead += OnNpcKilled;

            On.Terraria.GameContent.BossDamageTracker.OnBossKilled += OnBossKilledHook;

            TShock.Log.Info($"[BossCommand] 插件已加载 v{PluginVersion} | 极简逻辑版");
        }

        public override ValueTask DisposeAsync(bool isDisposing)
        {
            if (isDisposing)
            {
                GeneralHooks.ReloadEvent -= OnConfigReload;
                HookEvents.Terraria.NPC.OnSpawn -= OnNpcSpawn;
                HookEvents.Terraria.NPC.checkDead -= OnNpcKilled;
                On.Terraria.GameContent.BossDamageTracker.OnBossKilled -= OnBossKilledHook;
                _bossTracker?.Dispose();
            }

            return base.DisposeAsync(isDisposing);
        }

        private static void LoadConfig(ReloadEventArgs? args = null)
        {
            try
            {
                Config = Configuration.Read();
                TShock.Log.Info("[BossCommand] 配置加载完成");
                args?.Player?.SendSuccessMessage("[BossCommand] 配置已重载");
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[BossCommand] 配置加载失败: {ex.Message}");
            }
        }

        private void OnConfigReload(ReloadEventArgs args) => LoadConfig(args);

        /// <summary>
        /// 对应上游的 ServerApi.Hooks.NpcSpawn。
        /// HookEvents.Terraria.NPC.OnSpawn 的 sender 就是刚生成好的 NPC 实例，
        /// args.root 就是它所属的世界。
        /// </summary>
        private void OnNpcSpawn(NPC npc, HookEvents.Terraria.NPC.OnSpawnEventArgs args)
        {
            if (!Config.Enabled) return;
            if (npc?.active != true) return;

            ServerContext? server = args.root?.ToServer();
            if (server is null) return;

            Configuration.BossCommandConfig? bossConfig = FindBossConfig(npc.type);
            if (bossConfig == null) return;

            _bossTracker.MarkAsAlive(server, npc.whoAmI, npc.type, bossConfig.Name);
        }

        /// <summary>
        /// 对应上游的 ServerApi.Hooks.NpcKilled。
        /// HookEvents.Terraria.NPC.checkDead 的注入点就是 NPC.checkDead 里 active = false 之前，
        /// 每次死亡刚好触发一次（与上游的 Hooks.NPC.Killed 等价）。
        /// </summary>
        private void OnNpcKilled(NPC npc, HookEvents.Terraria.NPC.checkDeadEventArgs args)
        {
            if (npc == null || npc.type <= 0 || !Config.Enabled) return;

            ServerContext? server = args.root?.ToServer();
            if (server is null) return;

            Configuration.BossCommandConfig? config = FindBossConfig(npc.type);
            if (config == null) return;

            if (BossTracker.MultiSegmentBossMap.ContainsKey(npc.type))
            {
                TShock.Log.Debug($"[BossCommand] 多体节Boss {npc.FullName} 体节死亡，等待OnBossKilled钩子处理");
                return;
            }

            // 与 OnBossKilled 互斥：同一次死亡只执行一次命令
            if (!TryClaimKill(server, npc))
            {
                TShock.Log.Debug($"[BossCommand] 该死亡已由其它钩子处理，跳过: {npc.FullName} (Idx:{npc.whoAmI})");
                return;
            }

            if (!_bossTracker.TryProcessDeath(server, npc.whoAmI, npc.type))
            {
                TShock.Log.Debug($"[BossCommand] 跳过重复或未追踪的死亡: {npc.FullName} (Idx:{npc.whoAmI}, Type:{npc.type})");
                return;
            }

            if (config.RequireSummoned && !WasBossSpawned(server, npc.type))
            {
                TShock.Log.Debug($"[BossCommand] 未记录生成，跳过: {npc.FullName}");
                return;
            }

            Task.Run(() => ProcessBossKill(server, config, npc));
        }

        private bool WasBossSpawned(ServerContext server, int npcType)
        {
            if (_bossTracker.WasEverSpawned(server, npcType))
                return true;

            if (BossTracker.MultiSegmentBossMap.TryGetValue(npcType, out var segmentTypes))
            {
                foreach (var type in segmentTypes)
                {
                    if (_bossTracker.WasEverSpawned(server, type))
                        return true;
                }
            }

            var customDef = NPCDamageTracker.CustomBossDefinitions[npcType];
            if (customDef != null && customDef.NPCTypes != null)
            {
                foreach (var type in customDef.NPCTypes)
                {
                    if (_bossTracker.WasEverSpawned(server, type))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 从 NPC 反查它属于哪个世界。
        /// OnBossKilled 的 detour 签名没有 RootContext（和上游一致），
        /// 而 UTSL 的 NPC 上也没有 root 成员（实测反射确认），所以只能按实例引用比对。
        /// </summary>
        internal static ServerContext? FindServerForNpc(NPC npc)
        {
            foreach (ServerContext server in UnifiedServerCoordinator.Servers)
            {
                NPC[]? npcs = server.Main?.npc;
                if (npcs == null || npc.whoAmI < 0 || npc.whoAmI >= npcs.Length) continue;
                if (ReferenceEquals(npcs[npc.whoAmI], npc)) return server;
            }

            return null;
        }

        private void OnBossKilledHook(
            On.Terraria.GameContent.BossDamageTracker.orig_OnBossKilled orig,
            BossDamageTracker self, NPC npc)
        {
            orig(self, npc);
            if (!Config.Enabled) return;

            Configuration.BossCommandConfig? config = FindBossConfig(npc.type);
            if (config == null) return;

            ServerContext? server = FindServerForNpc(npc);
            if (server is null) return;

            // 与 OnNpcKilled 互斥：同一次死亡只执行一次命令
            if (!TryClaimKill(server, npc))
            {
                TShock.Log.Debug($"[BossCommand] OnBossKilled: 该死亡已由 NpcKilled 处理，跳过: {config.Name}");
                return;
            }

            if (config.RequireSummoned && !WasBossSpawned(server, npc.type))
            {
                TShock.Log.Debug($"[BossCommand] OnBossKilled: 未记录生成，跳过: {config.Name}");
                return;
            }

            TShock.Log.Info($"[BossCommand] OnBossKilled钩子触发: {config.Name}");
            Task.Run(() => ProcessBossKill(server, config, npc));

            if (Config.AutoBroadcastDamageRanking)
            {
                _damageRanker.Broadcast(server, self, npc);
            }
        }

        private async Task ProcessBossKill(ServerContext server, Configuration.BossCommandConfig config, NPC npc)
        {
            try
            {
                TShock.Log.Info($"[BossCommand] Boss {config.Name} 被击败，开始执行命令...");

                var commands = BuildCommandList(config);
                if (commands.Count == 0) return;

                int successCount = 0;
                foreach (var cmd in commands)
                {
                    if (await _commandProcessor.ExecuteAsync(server, cmd, config, npc))
                        successCount++;
                    if (Config.CommandDelay > 0)
                        await Task.Delay(Config.CommandDelay);
                }

                if (config.RecordExecutionCount) config.ExecutionCount++;
                Config.Write();

                if (config.BroadcastResult && successCount > 0 && Config.BroadcastEnabled)
                {
                    string msg = Config.BroadcastFormat
                        .Replace("{boss}", config.Name)
                        .Replace("{count}", successCount.ToString());
                    foreach (var player in ActivePlayers(server))
                    {
                        player.SendMessage(msg, Config.BroadcastColor.R, Config.BroadcastColor.G, Config.BroadcastColor.B);
                    }
                }

                _floatingTextService.ShowForBoss(server, config, npc);
                TShock.Log.Info($"[BossCommand] {config.Name} 执行完成: {successCount}/{commands.Count}");
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[BossCommand] 处理击杀失败: {ex}");
            }
        }

        /// <summary>UTSL 多世界：TShock.Players 是全局的，要按世界过滤</summary>
        private static IEnumerable<TSPlayer> ActivePlayers(ServerContext server)
            => TShock.Players.Where(p => p?.Active == true && ReferenceEquals(p.GetCurrentServer(), server));

        private List<string> BuildCommandList(Configuration.BossCommandConfig config)
        {
            var commands = new List<string>();

            if (!config.FirstKillDone && config.FirstKillCommands?.Count > 0)
            {
                commands.AddRange(config.FirstKillCommands);
                config.FirstKillDone = true;
                TShock.Log.Info($"[BossCommand] 触发首次击杀命令");
            }

            if (config.Commands?.Count > 0)
                commands.AddRange(config.Commands);

            return commands;
        }

        private Configuration.BossCommandConfig? FindBossConfig(int npcId)
        {
            return Config.BossCommands?.FirstOrDefault(b => b.BossIDs?.Contains(npcId) == true);
        }
    }
}
