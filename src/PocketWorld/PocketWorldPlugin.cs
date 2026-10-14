using System.Collections.Concurrent;
using System.Collections.Immutable;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Plugins;
using UnifierTSL.PluginService;
using UnifierTSL.Servers;
using UTSL.VirtualWorld;

namespace PocketWorld;

/// <summary>
/// 尘歌壶 —— 每个玩家一个隔离的私人世界。
///
/// 设计（v0.5.0，VirtualWorldCore 重构后）：
///   · 假世界基础设施（实例注册表 / 生命周期 / 玩家进出与状态一致性 / 看护与诊断）
///     已抽到 VirtualWorldCore 服务插件（见 50 号草案）；本插件只剩【玩法】：
///     访问策略、上限、世界名与文件名、种子选择、出生平台、聊天互通、状态同步、闲置计时。
///   · 按需创建：玩家第一次 /cg 时建立属于他的壶；
///   · 闲置销毁：壶里没人超过「闲置销毁_秒」→ 调核心 DestroyAsync（存档 + Close + RemoveServer）。
/// </summary>
[PluginMetadata("PocketWorld", "0.5.0", "星梦", "尘歌壶：每个玩家一个隔离的私人世界（按需创建、闲置销毁）")]
public sealed class PocketWorldPlugin : BasePlugin
{
    private static PocketWorldConfig _config = new();
    private static string _configPath = "";
    private static int _inited;
    private static Timer? _idleTimer;
    private static Timer? _syncTimer;

    /// <summary>VirtualWorldCore 服务（BeforeGlobalInitialize 里取）。</summary>
    private static IVirtualWorldCore? _core;

    /// <summary>契约版本不匹配标记（只用于日志）。</summary>
    private static bool _apiVersionMismatch;

    /// <summary>玩法侧的每壶状态。核心管生命周期/占用/返回路径；这里只放尘歌壶自己的策略数据。</summary>
    private sealed class Pot
    {
        public string Key = "";
        public string OwnerName = "";
        public string WorldName = "";
        public bool Busy;
        public long BusySinceMs;
        public bool Destroying;          // 正在闲置卸载（与"创建中"分开，避免把访问误判成"正在准备"）
        public long DestroySinceMs;
        public bool Destroyed;           // 世界已被卸载（meta 保留到没人用，便于访问者等待后重建）
        public string LastSig = "";
        public long LastActiveMs;
    }

    private static readonly ConcurrentDictionary<string, Pot> Pots = new(StringComparer.Ordinal);

    private static long NowMs => Environment.TickCount64;

    /// <summary>
    /// 服务发现：宿主在【所有插件的 InitializeAsync 之前】调用本方法，并传入完整插件容器列表
    /// （DotnetPluginHost.Initialize.cs:26-33）⇒ 与初始化顺序无关。
    /// </summary>
    public override void BeforeGlobalInitialize(ImmutableArray<IPluginContainer> plugins)
    {
        try
        {
            foreach (IPluginContainer c in plugins)
            {
                // ★ 只认【契约接口】，不绑定核心插件的具体类型名
                if (c.Plugin is IVirtualWorldCoreProvider provider)
                {
                    _apiVersionMismatch = provider.ApiVersion != VirtualWorldApi.Version;
                    _core = _apiVersionMismatch ? null : provider.Api;
                    break;
                }
            }
        }
        catch { _core = null; }
    }

    public override Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        try { _configPath = Path.Combine(configRegistrar.Directory, "settings.json"); } catch { _configPath = ""; }
        _config = PocketWorldConfig.Load(_configPath);

        if (Interlocked.Exchange(ref _inited, 1) == 1) { return Task.CompletedTask; }

        Prefs.Init(Path.Combine(_configPath.Length > 0 ? Path.GetDirectoryName(_configPath)! : ".", "players.json"));

        _syncTimer = new Timer(_ => SyncTick(), null, 2000, 2000);

        Commands.ChatCommands.Add(new Command("", Command, "cg"));
        PlayerHooks.PlayerLogout += OnLogout;
        PlayerHooks.PlayerChat += OnChat;

        if (_config.IdleDestroySeconds > 0)
        {
            _idleTimer = new Timer(_ => IdleTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        TShock.Log.Info($"[尘歌壶] 已加载（上限={_config.MaxPots}，闲置销毁={_config.IdleDestroySeconds}s，指令=/{"cg"}）");
        TShock.Log.Info(_core is not null
            ? $"[尘歌壶] 服务发现: 已取得 VirtualWorldCore（契约 v{VirtualWorldApi.Version}）"
            : _apiVersionMismatch
                ? $"[尘歌壶] 服务发现: ★契约版本不匹配（期望 v{VirtualWorldApi.Version}），假世界功能已禁用"
                : "[尘歌壶] 服务发现: ★未取得 VirtualWorldCore（核心未加载）");
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            try { PlayerHooks.PlayerLogout -= OnLogout; } catch { }
            try { PlayerHooks.PlayerChat -= OnChat; } catch { }
            try { _syncTimer?.Dispose(); } catch { }
            try { _idleTimer?.Dispose(); } catch { }
        }
        return ValueTask.CompletedTask;
    }

    private static void Command(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";

        // 顺手清掉"已登出但记录还在"的残留：只清离线玩家（铁律③），在线玩家一律不动
        _core?.Reconcile();

        switch (sub)
        {
            case "exit":
            case "leave":
                Leave(args);
                break;
            case "status":
                Status(args);
                break;
            case "contact":
            case "msg":
                ToggleContact(args);
                break;
            case "sync":
                ToggleSync(args);
                break;
            case "visit":
            case "join":
                Visit(args);
                break;
            default:
                Enter(args);
                break;
        }
    }

    // ===== 与主世界同步状态（时间持续 / 天气与进度变化时才推） =====

    /// <summary>同步内容：天气（双向复制，会因为雨停而变回 false）。</summary>
    private static readonly string[] WeatherFields =
    {
        "raining", "maxRaining", "numClouds", "windSpeedCurrent", "windSpeedTarget",
    };

    /// <summary>同步内容：主世界进度标记（只向 true 方向同步，避免把已达成"还原"）。</summary>
    private static readonly string[] MainProgressFields = { "hardMode" };

    /// <summary>同步内容：Boss / 事件进度（只向 true 方向同步）。</summary>
    private static readonly string[] NpcProgressFields =
    {
        "downedBoss1", "downedBoss2", "downedBoss3", "downedSlimeKing", "downedQueenBee", "downedFishron",
        "downedMartians", "downedAncientCultist", "downedMoonlord", "downedHalloweenKing", "downedHalloweenTree",
        "downedChristmasIceQueen", "downedChristmasSantank", "downedChristmasTree", "downedGolemBoss",
        "downedPirates", "downedFrost", "downedGoblins", "downedClown", "downedPlantBoss",
        "downedMechBoss1", "downedMechBoss2", "downedMechBoss3", "downedMechBossAny",
        "downedEmpressOfLight", "downedQueenSlime", "downedDeerclops",
        "downedTowerSolar", "downedTowerVortex", "downedTowerNebula", "downedTowerStardust",
    };

    /// <summary>该实例是不是【本玩法的壶】。（核心只管占用，不区分归属；归属由玩法自己判）</summary>
    private static bool IsMyPot(IVirtualWorld? w) => w is not null && Pots.ContainsKey(w.Key);

    /// <summary>主世界 = 非壶世界里的第一个在运行的世界。</summary>
    private static ServerContext? MainWorld() =>
        UnifiedServerCoordinator.Servers.FirstOrDefault(x => x.IsRunning && !x.Name.StartsWith("Pot_", StringComparison.Ordinal));

    private static string OwnerKeyByIndex(int idx)
    {
        TSPlayer? tp = TShock.Players.ElementAtOrDefault(idx);
        return tp is null ? "idx:" + idx : OwnerKey(tp);
    }

    /// <summary>反射复制同名字段；onlyTrue 时只复制值为 true 的（进度只前进）。</summary>
    private static int CopyFields(object src, object dst, string[] names, bool onlyTrue)
    {
        int n = 0;
        Type ts = src.GetType(), td = dst.GetType();
        foreach (string name in names)
        {
            try
            {
                var fsrc = ts.GetField(name);
                var fdst = td.GetField(name);
                if (fsrc is null || fdst is null) { continue; }
                object? v = fsrc.GetValue(src);
                if (onlyTrue && v is bool b && !b) { continue; }
                fdst.SetValue(dst, v);
                n++;
            }
            catch { }
        }
        return n;
    }

    /// <summary>轻量签名：天气 + 进度。签名不变就什么都不做（不轮询写入）。</summary>
    private static string Signature(ServerContext src)
    {
        var sb = new System.Text.StringBuilder(64);
        foreach (string f in WeatherFields)
        {
            try { sb.Append(src.Main.GetType().GetField(f)?.GetValue(src.Main)).Append('|'); } catch { }
        }
        foreach (string f in MainProgressFields)
        {
            try { sb.Append(src.Main.GetType().GetField(f)?.GetValue(src.Main) is true ? '1' : '0'); } catch { }
        }
        foreach (string f in NpcProgressFields)
        {
            try { sb.Append(src.NPC.GetType().GetField(f)?.GetValue(src.NPC) is true ? '1' : '0'); } catch { }
        }
        return sb.ToString();
    }

    private static void ToggleSync(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }
        var prefs = Prefs.Get(OwnerKey(p));
        prefs.Sync = !prefs.Sync;
        Prefs.Save();
        p.SendInfoMessage($"[尘歌壶] 与主世界同步状态：{(prefs.Sync ? "已开启（时间持续同步，天气与进度变化时同步）" : "已关闭")}");
        if (prefs.Sync) { SyncNow(p.Index); }        // ★ 开启瞬间立即同步一次
    }

    /// <summary>立即把主世界状态复制到该玩家所在的壶。</summary>
    private static void SyncNow(int plrIndex)
    {
        if (_core is null) { return; }
        IVirtualWorld? w = _core.FindByOccupant(plrIndex);
        ServerContext? dst = w?.Context;
        if (dst is null) { return; }
        ServerContext? src = MainWorld();
        if (src is null) { return; }
        if (Pots.TryGetValue(w!.Key, out Pot? pot)) { pot.LastSig = ""; }
        dst.Dispatcher.Post(() =>
        {
            try
            {
                dst.Main.time = src.Main.time;
                dst.Main.dayTime = src.Main.dayTime;
                dst.Main.moonPhase = src.Main.moonPhase;
                CopyFields(src.Main, dst.Main, WeatherFields, onlyTrue: false);
                CopyFields(src.Main, dst.Main, MainProgressFields, onlyTrue: true);
                CopyFields(src.NPC, dst.NPC, NpcProgressFields, onlyTrue: true);
            }
            catch (Exception ex) { TShock.Log.Warning("[尘歌壶] 立即同步失败: " + ex.Message); }
        });
    }

    /// <summary>定时器：每 2 秒；时间持续对齐，天气/进度仅在签名变化时推送。</summary>
    private static void SyncTick()
    {
        try
        {
            if (_core is null) { return; }
            ServerContext? src = MainWorld();
            if (src is null) { return; }
            string sig = Signature(src);
            double t = src.Main.time;
            bool day = src.Main.dayTime;
            int moon = src.Main.moonPhase;

            foreach (Pot pot in Pots.Values)
            {
                IVirtualWorld? w = _core.Find(pot.Key);
                ServerContext? dst = w?.Context;
                if (dst is null || !dst.IsRunning) { continue; }

                bool want = false;
                foreach (int idx in w!.Occupants)
                {
                    if (Prefs.Get(OwnerKeyByIndex(idx)).Sync) { want = true; break; }
                }
                if (!want) { continue; }

                bool changed = pot.LastSig != sig;
                pot.LastSig = sig;
                dst.Dispatcher.Post(() =>
                {
                    try
                    {
                        dst.Main.time = t;
                        dst.Main.dayTime = day;
                        dst.Main.moonPhase = moon;
                        if (changed)
                        {
                            CopyFields(src.Main, dst.Main, WeatherFields, onlyTrue: false);
                            CopyFields(src.Main, dst.Main, MainProgressFields, onlyTrue: true);
                            CopyFields(src.NPC, dst.NPC, NpcProgressFields, onlyTrue: true);
                        }
                    }
                    catch (Exception ex) { TShock.Log.Warning("[尘歌壶] 同步失败: " + ex.Message); }
                });
            }
        }
        catch { }
    }

    /// <summary>切换"与主世界保持联系"（壶里的发言是否转发到主世界，双向）。</summary>
    private static void ToggleContact(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }
        var prefs = Prefs.Get(OwnerKey(p));
        prefs.Contact = !prefs.Contact;
        Prefs.Save();
        p.SendInfoMessage($"[尘歌壶] 与主世界保持联系：{(prefs.Contact ? "已开启（聊天双向互通）" : "已关闭")}");
    }

    /// <summary>
    /// 聊天转发：壶里开了"保持联系"的玩家与主世界双向互通；主世界之间也互通。
    /// 不抑制原生广播，只做"跨世界补充投递"，同世界跳过以免重复。
    /// </summary>
    private static void OnChat(PlayerChatEventArgs e)
    {
        try
        {
            TSPlayer? p = e.Player;
            if (p is null || !p.Active || _core is null) { return; }
            string text = e.RawText ?? "";
            if (string.IsNullOrWhiteSpace(text)) { return; }

            ServerContext? here = UnifiedServerCoordinator.GetClientCurrentlyServer(p.Index);
            if (here is null) { return; }

            bool speakerInPot = IsMyPot(_core.FindByOccupant(p.Index));
            if (speakerInPot && !Prefs.Get(OwnerKey(p)).Contact) { return; }

            string line = (speakerInPot ? "[尘歌壶] " : "[主世界] ") + p.Name + ": " + text;

            foreach (TSPlayer tp in TShock.Players)
            {
                if (tp is null || !tp.Active || tp.Index == p.Index) { continue; }
                ServerContext? tpWorld = UnifiedServerCoordinator.GetClientCurrentlyServer(tp.Index);
                if (tpWorld is null || ReferenceEquals(tpWorld, here)) { continue; }   // 同世界：原生已收到

                bool tpInPot = IsMyPot(_core.FindByOccupant(tp.Index));
                if (speakerInPot)
                {
                    if (!tpInPot) { tp.SendMessage(line, 255, 255, 255); }             // 壶 -> 主世界
                }
                else if (!tpInPot)
                {
                    tp.SendMessage(line, 255, 255, 255);                              // 主世界之间互通
                }
                else if (Prefs.Get(OwnerKey(tp)).Contact)
                {
                    tp.SendMessage(line, 255, 255, 255);                              // 主世界 -> 开了联系的壶
                }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[尘歌壶] 聊天转发异常: " + ex.Message); }
    }

    // ===== 玩法：确保壶世界存在并加载（种子选择/出生平台/清空策略都由玩法决定） =====

    private static Task<IVirtualWorld> EnsurePotAsync(Pot pot, ServerContext from)
    {
        int tileW = from.Main.maxTilesX, tileH = from.Main.maxTilesY;
        ServerContext seedWorld = UnifiedServerCoordinator.Servers.FirstOrDefault(
            x => x.IsRunning && !x.Name.StartsWith("Pot_", StringComparison.Ordinal)) ?? from;

        return _core!.EnsureAsync(new VirtualWorldRequest
        {
            Key = pot.Key,
            DisplayName = pot.WorldName,
            OwnerKey = pot.Key,
            ServerName = "Pot_" + ShortKey(pot.Key),
            WorldFileName = WorldFileName(pot.OwnerName, pot.Key),
            TilesX = tileW,
            TilesY = tileH,
            SeedWorldPath = seedWorld.Main.worldPathName,
            Headless = true,
            IdleDestroySeconds = _config.IdleDestroySeconds,
            LogTag = "[尘歌壶]",
            // ★ 只有"这次刚刚拿种子建的世界"才清空；加载既有存档时绝不清空，否则会把玩家建的东西抹掉
            OnLoaded = static (ctx, justSeeded) => { if (justSeeded) { ctx.ClearCurrentWorld(); } },
            // ★ 每次进入都确保出生点有落脚平台（世界文件可能来自任何历史状态）
            OnBeforeEnter = static (ctx, _) =>
            {
                ctx.BuildSpawnPlatform(20);
                try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); } catch { }
            },
        }, CancellationToken.None);
    }

    /// <summary>访问他人的尘歌壶（需要权限）。对方壶已被闲置销毁时直接重建。</summary>
    private static void Visit(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { TShock.Log.Info("[尘歌壶] 该指令需要玩家执行"); return; }
        if (args.Parameters.Count < 2)
        {
            p.SendInfoMessage("[尘歌壶] 用法：/cg visit <玩家名>（访问他人的尘歌壶）");
            return;
        }
        if (!p.HasPermission("tshock.admin"))
        {
            p.SendErrorMessage("[尘歌壶] 你没有访问他人尘歌壶的权限。");
            return;
        }

        string want = args.Parameters[1];
        if (string.Equals(want, p.Name, StringComparison.OrdinalIgnoreCase))
        {
            p.SendInfoMessage("[尘歌壶] 那是你自己的壶，直接用 /cg 即可。");
            return;
        }

        TSPlayer? owner = TShock.Players.FirstOrDefault(x => x is not null && x.Active && string.Equals(x.Name, want, StringComparison.OrdinalIgnoreCase));
        string key;
        string ownerName;
        if (owner is not null)
        {
            key = OwnerKey(owner);
            ownerName = owner.Name;
        }
        else
        {
            // 离线壶主：按世界文件名 尘歌壶_<名字>_<短键>.wld 找他已持久化的壶
            string wdir = Terraria.Main.WorldPath;
            string prefix = "尘歌壶_" + want + "_";
            string[] found;
            try { found = Directory.GetFiles(wdir, prefix + "*.wld"); } catch { found = []; }
            if (found.Length == 0)
            {
                p.SendErrorMessage($"[尘歌壶] 找不到 {want} 的壶（他可能从未创建过）。");
                return;
            }
            key = Path.GetFileNameWithoutExtension(found[0]).Substring(prefix.Length);   // = 短键，与 ShortKey 结果一致
            ownerName = want;
            p.SendInfoMessage($"[尘歌壶] {want} 当前不在线，将访问他留下的壶。");
        }

        // 若内存里已有同名壶，复用它的 key，避免重复创建
        string wantWorld = ownerName + "的尘歌壶";
        Pot? dup = Pots.Values.FirstOrDefault(v => string.Equals(v.WorldName, wantWorld, StringComparison.Ordinal));
        if (dup is not null) { key = dup.Key; }

        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(p.Index);
        if (from is null) { p.SendErrorMessage("[尘歌壶] 找不到你当前所在的世界。"); return; }
        if (_core is null) { p.SendErrorMessage("[尘歌壶] 核心服务不可用。"); return; }

        Pots.TryGetValue(key, out Pot? pot);
        if (pot is not null && pot.Busy) { p.SendInfoMessage("[尘歌壶] 对方的壶正在准备中，请稍候…"); return; }
        if (pot is not null && pot.Destroying) { p.SendInfoMessage("[尘歌壶] 对方的壶正在卸载中，等它完成后会为你重建，请稍候…"); }
        if (pot is null)
        {
            if (Pots.Values.Count(v => !v.Destroyed) >= Math.Max(1, _config.MaxPots))
            {
                p.SendErrorMessage($"[尘歌壶] 当前壶数已达上限（{_config.MaxPots}），请稍后再试。");
                return;
            }
            pot = new Pot { Key = key, OwnerName = ownerName, WorldName = wantWorld, Busy = true, BusySinceMs = NowMs, LastActiveMs = NowMs };
            Pots[key] = pot;
        }
        else
        {
            pot.Busy = true;
            pot.LastActiveMs = NowMs;
        }

        bool rebuild = _core.Find(key)?.IsLoaded != true;
        int tileW = from.Main.maxTilesX, tileH = from.Main.maxTilesY;
        p.SendInfoMessage(rebuild
            ? $"[尘歌壶] {ownerName} 的壶已卸载，正在为他重建…"
            : $"[尘歌壶] 正在前往 {ownerName} 的尘歌壶…");
        TShock.Log.Info($"[尘歌壶] {p.Name} 访问 {ownerName} 的壶（重建={rebuild}，{tileW}x{tileH}）");

        Pot potRef = pot;
        string ownerNameRef = ownerName;
        Task.Run(async () =>
        {
            try
            {
                if (!await WaitDestroyAsync(potRef).ConfigureAwait(false))
                {
                    potRef.Busy = false;
                    p.SendErrorMessage("[尘歌壶] 对方的壶仍在卸载中，请稍后重试。");
                    return;
                }
                IVirtualWorld world = await EnsurePotAsync(potRef, from).ConfigureAwait(false);
                bool ok = await _core!.EnterAsync(p, world, CancellationToken.None).ConfigureAwait(false);
                potRef.Busy = false;
                potRef.Destroyed = false;
                potRef.LastActiveMs = NowMs;
                if (ok)
                {
                    p.SendSuccessMessage($"[尘歌壶] 已进入 {ownerNameRef} 的尘歌壶。用 /{"cg"} exit 返回。");
                }
                else
                {
                    p.SendErrorMessage("[尘歌壶] 进入失败：世界转移被拒绝，请稍后再试。");
                }
            }
            catch (Exception ex)
            {
                potRef.Busy = false;
                Pots.TryRemove(potRef.Key, out _);
                TShock.Log.Error("[尘歌壶] 访问流程异常: " + ex);
                p.SendErrorMessage("[尘歌壶] 准备失败: " + ex.Message);
            }
        });
    }

    private static void Status(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        p?.SendInfoMessage($"[尘歌壶] 当前 {Pots.Count}/{_config.MaxPots} 个壶：");
        foreach (Pot pot in Pots.Values)
        {
            IVirtualWorld? w = _core?.Find(pot.Key);
            bool inside = _core is not null && w is not null && _core.CountLiveOccupants(w) > 0;
            p?.SendInfoMessage($"  {pot.WorldName} {(inside ? "（有人在里面）" : "（空）")}");
        }

        p?.SendInfoMessage($"[尘歌壶] 用法：/{"cg"} 进入 · {"cg"} exit 离开 · {"cg"} status");
    }

    private static void Enter(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { TShock.Log.Info("[尘歌壶] 该指令需要玩家执行"); return; }

        string key = OwnerKey(p);
        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(p.Index);
        if (from is null) { p.SendErrorMessage("[尘歌壶] 找不到你当前所在的世界。"); return; }
        if (_core is null) { p.SendErrorMessage("[尘歌壶] 核心服务不可用。"); return; }

        _core.SelfHeal(p.Index);        // 只在"进入"路径自愈，避免误伤 /cg exit
        IVirtualWorld? occ = _core.FindByOccupant(p.Index);
        if (IsMyPot(occ))
        {
            p.SendInfoMessage("[尘歌壶] 你已经在自己的壶里了。用 /" + "cg" + " exit 返回。");
            return;
        }
        if (occ is not null)
        {
            p.SendInfoMessage($"[尘歌壶] 你当前在「{occ.DisplayName}」里，请先退出那里再进壶。");
            return;
        }

        Pots.TryGetValue(key, out Pot? pot);
        if (pot is not null && pot.Busy)
        {
            p.SendInfoMessage("[尘歌壶] 你的壶正在准备中，请稍候…");
            return;
        }
        if (pot is not null && pot.Destroying)
        {
            p.SendInfoMessage("[尘歌壶] 你的壶正在卸载中，等它完成后会为你重建，请稍候…");
        }
        if (pot is null)
        {
            if (Pots.Values.Count(v => !v.Destroyed) >= Math.Max(1, _config.MaxPots))
            {
                p.SendErrorMessage($"[尘歌壶] 当前壶数已达上限（{_config.MaxPots}），请稍后再试。");
                return;
            }
            pot = new Pot { Key = key, OwnerName = p.Name, WorldName = MakeWorldName(p), Busy = true, BusySinceMs = NowMs, LastActiveMs = NowMs };
            Pots[key] = pot;
        }
        else
        {
            pot.Busy = true;
            pot.LastActiveMs = NowMs;
        }

        int tileW = from.Main.maxTilesX, tileH = from.Main.maxTilesY;
        p.SendInfoMessage("[尘歌壶] 正在为你准备壶中世界…（首次进入需要加载，请稍候）");
        TShock.Log.Info($"[尘歌壶] {p.Name} 请求进入（世界={pot.WorldName}，{tileW}x{tileH}）");

        Pot potRef = pot;
        Task.Run(async () =>
        {
            try
            {
                if (!await WaitDestroyAsync(potRef).ConfigureAwait(false))
                {
                    potRef.Busy = false;
                    p.SendErrorMessage("[尘歌壶] 壶仍在卸载中，请稍后重试。");
                    return;
                }
                IVirtualWorld world = await EnsurePotAsync(potRef, from).ConfigureAwait(false);
                bool ok = await _core!.EnterAsync(p, world, CancellationToken.None).ConfigureAwait(false);
                potRef.Busy = false;
                potRef.Destroyed = false;
                potRef.LastActiveMs = NowMs;
                if (ok)
                {
                    p.SendSuccessMessage($"[尘歌壶] 已进入你的壶中世界（{potRef.WorldName}）。用 /{"cg"} exit 返回。");
                }
                else
                {
                    p.SendErrorMessage("[尘歌壶] 进入失败：世界转移被拒绝，请稍后再试。");
                }
            }
            catch (Exception ex)
            {
                potRef.Busy = false;
                Pots.TryRemove(potRef.Key, out _);
                TShock.Log.Error("[尘歌壶] 进入流程异常: " + ex);
                p.SendErrorMessage("[尘歌壶] 准备失败: " + ex.Message);
            }
        });
    }

    private static void Leave(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null || _core is null) { return; }

        IVirtualWorld? occ = _core.FindByOccupant(p.Index);
        if (!IsMyPot(occ))
        {
            p.SendInfoMessage(occ is null
                ? "[尘歌壶] 你当前不在壶里。"
                : $"[尘歌壶] 你当前不在壶里（在「{occ.DisplayName}」）。");
            return;
        }

        p.SendInfoMessage("[尘歌壶] 正在返回…");
        Task.Run(async () =>
        {
            try
            {
                bool ok = await _core.LeaveAsync(p, CancellationToken.None).ConfigureAwait(false);
                if (ok) { p.SendSuccessMessage("[尘歌壶] 已返回原世界。"); }
                else { p.SendErrorMessage("[尘歌壶] 返回失败：世界转移被拒绝，请稍后再试。"); }
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[尘歌壶] 返回失败: " + ex);
                p.SendErrorMessage("[尘歌壶] 返回失败: " + ex.Message);
            }
        });
    }

    /// <summary>若该壶正在闲置卸载，等它结束（Close 等超时，正常约 60s），再让访问者重建。</summary>
    private static async Task<bool> WaitDestroyAsync(Pot pot)
    {
        long waited = 0;
        while (pot.Destroying && waited < 75000)
        {
            await Task.Delay(500).ConfigureAwait(false);
            waited += 500;
        }
        if (pot.Destroying) { return false; }   // 超时仍未卸载完：绝不往"正在关闭的世界"里搬人
        pot.Busy = true;                        // 等待期间可能被卡死保护清掉
        return true;
    }

    private static void OnLogout(PlayerLogoutEventArgs e)
    {
        try
        {
            if (e.Player is null || _core is null) { return; }
            IVirtualWorld? w = _core.NotifyPlayerGone(e.Player.Index);
            TShock.Log.Info(w is not null
                ? $"[尘歌壶] 登出钩子: {e.Player.Name}(idx={e.Player.Index}) -> 命中实例 {w.DisplayName}"
                : $"[尘歌壶] 登出钩子: {e.Player.Name}(idx={e.Player.Index}) -> 未命中任何实例");
            if (w is not null && Pots.TryGetValue(w.Key, out Pot? pot))
            {
                pot.Busy = false;
                pot.LastActiveMs = NowMs;
                TShock.Log.Info($"[尘歌壶] {e.Player.Name} 登出，{pot.WorldName} 转为空闲（闲置到期后销毁）");
            }
        }
        catch { }
    }

    private static void IdleTick()
    {
        try
        {
            if (_core is null) { return; }
            _core.Reconcile();
            _core.StateCheck();

            long now = NowMs;
            long limit = _config.IdleDestroySeconds * 1000L;
            foreach (var kv in Pots.ToArray())
            {
                Pot pot = kv.Value;

                // Busy 卡死保护：创建过程中玩家断线会让 Busy 永远为 true
                if (pot.Destroyed)
                {
                    // 世界已卸载；meta 保留到没人再用（访问者可能正在等它、准备重建）
                    if (!pot.Busy && !pot.Destroying) { Pots.TryRemove(kv.Key, out _); }
                    continue;
                }
                if (pot.Busy)
                {
                    if (now - pot.BusySinceMs > 180000)
                    {
                        TShock.Log.Warning($"[尘歌壶] {pot.WorldName} 准备超时，已解除占用");
                        pot.Busy = false;
                    }
                    continue;
                }
                if (pot.Destroying)
                {
                    if (now - pot.DestroySinceMs > 180000)   // 正常约 60s（Close 等超时）
                    {
                        TShock.Log.Warning($"[尘歌壶] {pot.WorldName} 卸载超时，已解除卸载标记");
                        pot.Destroying = false;
                    }
                    continue;
                }

                IVirtualWorld? world = _core.Find(pot.Key);
                if (world is null) { Pots.TryRemove(kv.Key, out _); continue; }

                // ★ 铁律②：以【真实在线且确实路由到该世界】的玩家数为准，不信记录
                if (_core.CountLiveOccupants(world) > 0) { continue; }
                if (now - pot.LastActiveMs < limit) { continue; }

                // ★ 双保险（世界玩家表里仍有 active 玩家就绝不销毁）在核心 DestroyAsync 里
                pot.Destroying = true;
                pot.DestroySinceMs = NowMs;
                string key = kv.Key;
                string name = pot.WorldName;
                TShock.Log.Info($"[尘歌壶] {name} 闲置超时，正在存档并卸载…");
                Task.Run(async () =>
                {
                    try
                    {
                        bool ok = await _core.DestroyAsync(world, save: true, CancellationToken.None).ConfigureAwait(false);
                        pot.Destroying = false;
                        if (ok) { pot.Destroyed = true; }
                        else { pot.LastActiveMs = NowMs; }
                    }
                    catch (Exception ex)
                    {
                        pot.Destroying = false;
                        TShock.Log.Error("[尘歌壶] 卸载异常: " + ex);
                    }
                });
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[尘歌壶] 闲置清理异常: " + ex.Message); }
    }

    private static int _keyLogged;

    /// <summary>
    /// 玩家标识：优先账号 UUID（改名不丢壶），其次连接 UUID，最后退回玩家名。
    /// 首次解析时打一行诊断，便于确认到底拿到了哪一个。
    /// </summary>
    private static string OwnerKey(TSPlayer p)
    {
        string account = "";
        try { account = p.Account?.UUID ?? ""; } catch { }

        string conn = "";
        try { conn = p.UUID ?? ""; } catch { }

        string key = !string.IsNullOrEmpty(account) ? "acct:" + account
                   : !string.IsNullOrEmpty(conn) ? "conn:" + conn
                   : "name:" + (p.Name ?? ("#" + p.Index));

        if (Interlocked.Exchange(ref _keyLogged, 1) == 0)
        {
            TShock.Log.Info($"[尘歌壶] 玩家标识诊断: name={p.Name} accountUUID='{account}' connUUID='{conn}' -> key={key}");
        }

        return key;
    }

    private static string ShortKey(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (char c in key) { if (char.IsLetterOrDigit(c)) { sb.Append(c); } }
        string k = sb.ToString();
        return k.Length > 12 ? k[..12] : k;
    }

    private static string MakeWorldName(TSPlayer p)
    {
        string n = (p.Name ?? "?").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) { n = n.Replace(c, '_'); }
        return n + "的尘歌壶";
    }

    // 世界文件名 = 尘歌壶_<玩家名>_<UUID前12>：既好看又能唯一定位（改名也不丢壶）
    private static string WorldFileName(string playerName, string key)
    {
        string n = (playerName ?? "?").Trim();
        return "尘歌壶_" + n + "_" + ShortKey(key);
    }
}
