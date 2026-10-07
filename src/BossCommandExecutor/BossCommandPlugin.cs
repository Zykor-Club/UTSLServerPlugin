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
        private readonly ConcurrentDictionary<string, long> _handledKills = new();

        private const long KillDedupWindowMs = 5_000;

        /// <summary>钩子触发时濒死体节还没被置为 inactive，延后一点再判定</summary>
        private const int KillSettleDelayMs = 400;

        /// <summary>
        /// 击杀认领键。
        /// 多体节 Boss 必须**整组共用一个键** —— 否则每个体节各认领各的，
        /// 而单个体节的判定又因为"自己还 active"永远失败，就会互相抵消导致命令一次都不执行
        /// （实测世界吞噬者就是这样：OnBossKilled 触发了、排行榜播了，但命令被 NpcKilled 的认领挡住）。
        /// </summary>
        private static string KillClaimKey(ServerContext server, NPC npc)
        {
            if (BossTracker.TryGetSegmentTypes(server, npc.type, out int[] types))
                return server.Name + ":grp:" + string.Join(",", types.OrderBy(t => t));

            return server.Name + ":npc:" + npc.whoAmI + ":" + npc.type;
        }

        private bool TryClaimKill(ServerContext server, NPC npc)
        {
            long now = Environment.TickCount64;

            foreach (KeyValuePair<string, long> kv in _handledKills)
            {
                if (now - kv.Value > KillDedupWindowMs)
                    _handledKills.TryRemove(kv.Key, out _);
            }

            return _handledKills.TryAdd(KillClaimKey(server, npc), now);
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

            _bossTracker.MarkAsAlive(server, npc.whoAmI, npc.type, bossConfig.Name, npc.lifeMax);
        }

        /// <summary>
        /// 对应上游的 ServerApi.Hooks.NpcKilled。
        ///
        /// ⚠️ 实测（UTSL）：HookEvents.Terraria.NPC.checkDead 是**每次受到伤害**都会被调用的
        /// （Terraria 内部用它判断"这个 NPC 该不该死"），**不是**只在死亡时调用。
        /// 所以必须先确认 life &lt;= 0，否则玩家第一次打到 Boss 就会误触发整套命令。
        /// </summary>
        private void OnNpcKilled(NPC npc, HookEvents.Terraria.NPC.checkDeadEventArgs args)
        {
            if (npc == null || npc.type <= 0 || !Config.Enabled) return;
            if (npc.life > 0) return;   // 还活着，只是挨了一下

            ServerContext? server = args.root?.ToServer();
            if (server is null) return;

            Configuration.BossCommandConfig? config = FindBossConfig(npc.type);
            if (config == null) return;

            TShock.Log.Info($"[BossCommand] checkDead 判定死亡: {npc.FullName} type={npc.type} idx={npc.whoAmI} life={npc.life}/{npc.lifeMax}");

            ServerContext ctx = server;
            NPC dying = npc;
            Task.Run(() => ProcessKillAfterDelayAsync(ctx, dying, config, "NpcKilled"));
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

            TShock.Log.Info($"[BossCommand] OnBossKilled 触发: {npc.FullName} type={npc.type} idx={npc.whoAmI} life={npc.life}/{npc.lifeMax}");

            // 伤害排行广播属于"伤害追踪"的职责，跟命令执行是两回事，所以紧挨着触发点立刻播。
            // 多体节 Boss 要显示整组的总血量，而不是单个体节的（实测世界吞噬者只显示 150）。
            if (Config.AutoBroadcastDamageRanking)
            {
                int displayLifeMax = _bossTracker.GetGroupLifeMax(server, npc.type, npc.lifeMax);
                _damageRanker.Broadcast(server, self, npc, displayLifeMax);
            }

            ServerContext ctx = server;
            NPC dead = npc;
            Task.Run(() => ProcessKillAfterDelayAsync(ctx, dead, config, "OnBossKilled"));
        }

        /// <summary>
        /// 真正判定"这一次 Boss 击杀"。
        ///
        /// 为什么必须延迟：OTAPI 的 checkDead / OnBossKilled 都在 Terraria 把 active 置 false
        /// **之前**触发，此刻濒死的体节自己还是 active=True。所以现场直接判断"还有没有体节存活"
        /// 永远为真 —— 多体节 Boss 会永远判不出死亡。
        /// 延后几百毫秒让世界把 active 置 false，再由 IsBossDefeated 统一判定。
        ///
        /// 两个钩子都会走到这里，靠"按体节组认领"（KillClaimKey）保证一次击杀只执行一次命令。
        /// </summary>
        private async Task ProcessKillAfterDelayAsync(
            ServerContext server,
            NPC npc,
            Configuration.BossCommandConfig config,
            string source)
        {
            try
            {
                await Task.Delay(KillSettleDelayMs);

                if (!_bossTracker.IsBossDefeated(server, npc.type))
                {
                    TShock.Log.Debug($"[BossCommand] {source}: 还有体节存活，不处理: {config.Name}");
                    return;
                }

                if (!TryClaimKill(server, npc))
                {
                    TShock.Log.Debug($"[BossCommand] {source}: 本次击杀已由其它钩子处理，跳过: {config.Name}");
                    return;
                }

                if (config.RequireSummoned && !WasBossSpawned(server, npc.type))
                {
                    TShock.Log.Debug($"[BossCommand] {source}: 未记录生成，跳过: {config.Name}");
                    _bossTracker.ClearGroupLife(server, npc.type);
                    return;
                }

                TShock.Log.Info($"[BossCommand] 判定 {config.Name} 已被击杀（来源 {source}）");
                await ProcessBossKill(server, config, npc);
                _bossTracker.ClearGroupLife(server, npc.type);
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[BossCommand] 处理击杀失败({source}): {ex}");
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
