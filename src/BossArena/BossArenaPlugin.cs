using System.Collections.Immutable;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Plugins;
using UnifierTSL.PluginService;
using UnifierTSL.Servers;
using UTSL.VirtualWorld;

namespace BossArena;

/// <summary>
/// Boss 场地 —— VirtualWorldCore 的「抽象试金石」。
///
/// 玩法本身很简单：一个公共假世界，进去是一块战斗平台；场地空了下一次进入时重置。
/// ★ 关键点：本插件【只使用 IVirtualWorldCore 的公开接口 + VirtualWorldRequest 的回调】，
///   用来回答"核心要不要改"。若要改，说明抽象还差一点。
/// </summary>
[PluginMetadata("BossArena", "0.2.0", "星梦", "Boss 场地：公共假世界 + 战斗平台模板 + 区域重置")]
public sealed class BossArenaPlugin : BasePlugin
{
    private const string ArenaKey = "arena:boss";
    private const string ArenaServerName = "BossArena";
    private const string ArenaWorldFile = "BossArena";
    private const int HalfWidth = 60;          // 战斗区域半宽（格）；区域 = [cx-60, cx+60]

    private static IVirtualWorldCore? _core;
    private static bool _apiVersionMismatch;
    private static BossArenaConfig _config = new();
    private static string _configPath = "";
    private static Timer? _sweepTimer;
    private static long _idleSinceMs;

    /// <summary>服务发现：与初始化顺序无关（宿主在所有 InitializeAsync 之前调用）。</summary>
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

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock") { await initInfo.InitializationTask; }
        }

        try { _configPath = Path.Combine(configRegistrar.Directory, "settings.json"); } catch { _configPath = ""; }
        _config = BossArenaConfig.Load(_configPath);

        // O3 演示/自测：订阅核心的可否决事件（默认不否决，靠配置开关打开）
        if (_core is not null)
        {
            try { _core.Transferring += OnTransferring; } catch { }
        }

        Commands.ChatCommands.Add(new Command("", Command, "ba"));

        // 闲置卸载：每 5 秒看一次，场地没人超过配置时长就存档并卸载（下次 /ba 自动重建）。
        _sweepTimer = new Timer(_ => SweepIdle(), null, 5000, 5000);
        TShock.Log.Info(_core is not null
            ? $"[BossArena] 已加载 v0.2.0（服务发现: 已取得 VirtualWorldCore，契约 v{VirtualWorldApi.Version}）"
            : _apiVersionMismatch
                ? $"[BossArena] 已加载 v0.2.0（★契约版本不匹配，期望 v{VirtualWorldApi.Version}，功能已禁用）"
                : "[BossArena] 已加载 v0.2.0（★未取得 VirtualWorldCore）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { try { _sweepTimer?.Dispose(); } catch { } }
        return ValueTask.CompletedTask;
    }

    /// <summary>闲置检查：场地已加载、且没有活着的玩家超过设定时长 → 存档并卸载。</summary>
    private static void SweepIdle()
    {
        try
        {
            if (_core is null || _config.IdleDestroySeconds <= 0) { return; }
            IVirtualWorld? w = _core.Find(ArenaKey);
            if (w is null || !w.IsLoaded) { _idleSinceMs = 0; return; }
            if (_core.CountLiveOccupants(w) > 0) { _idleSinceMs = 0; return; }

            long now = Environment.TickCount64;
            if (_idleSinceMs == 0) { _idleSinceMs = now; return; }
            if (now - _idleSinceMs < _config.IdleDestroySeconds * 1000L) { return; }

            _idleSinceMs = 0;
            IVirtualWorld world = w;
            Task.Run(async () =>
            {
                try
                {
                    VirtualWorldResult r = await _core!.DestroyAsync(world, save: true, CancellationToken.None).ConfigureAwait(false);
                    TShock.Log.Info($"[BossArena] 闲置卸载: success={r.Success} {r.Message}（闲置 {_config.IdleDestroySeconds}s）");
                }
                catch (Exception ex) { TShock.Log.Warning("[BossArena] 闲置卸载异常: " + ex.Message); }
            });
        }
        catch (Exception ex) { TShock.Log.Warning("[BossArena] 闲置检查异常: " + ex.Message); }
    }

    /// <summary>可否决事件回调：只在本插件被开启【调试_否决进入】时否决"进入场地"。</summary>
    private static void OnTransferring(VirtualWorldTransferArgs args)
    {
        if (!_config.DebugVetoEnter) { return; }
        if (args.IsEnter && args.World.Group == "BossArena")
        {
            args.Cancel = true;
            args.CancelReason = "[调试] BossArena 否决进入（调试_否决进入 = true）";
        }
    }

    /// <summary>该实例是不是【本玩法的 Boss 场地】。</summary>
    private static bool IsArena(IVirtualWorld? w) => w is not null && w.Key == ArenaKey;

    /// <summary>假世界互通判定：玩家目前所在的【另一个假世界】是否允许他直接进场地。</summary>
    private static bool InteropAllows(IVirtualWorld? from)
    {
        if (from is null) { return true; }
        if (!_config.InteropAllowEnter) { return false; }
        string[] allow = _config.InteropAllowedSources;
        return allow.Length == 0 || allow.Contains(from.Group);
    }

    /// <summary>场地的玩法策略：核心在进入/销毁前回调。</summary>
    private sealed class ArenaPolicy : IVirtualWorldPolicy
    {
        public bool CanJoin(TSPlayer player, IVirtualWorld world) => InteropAllows(_core?.FindByOccupant(player.Index));
        public bool CanDestroy(IVirtualWorld world) => true;
        public void OnLoaded(IVirtualWorld world) { }
        public void OnPlayerEntered(TSPlayer player, IVirtualWorld world) { }
        public void OnPlayerLeft(TSPlayer player, IVirtualWorld world) { }
    }

    private static void Command(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";
        _core?.Reconcile();      // 顺手清掉已离线玩家的残留记录（只清离线，铁律③）
        switch (sub)
        {
            case "exit":
            case "leave":
                Leave(args);
                break;
            case "reset":
                Reset(args);
                break;
            case "status":
                Status(args);
                break;
            default:
                Enter(args);
                break;
        }
    }

    // ===== 进入 / 离开（全部交给核心） =====

    private static void Enter(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { TShock.Log.Info("[BossArena] 该指令需要玩家执行"); return; }
        if (_core is null) { p.SendErrorMessage("[BossArena] 核心服务不可用。"); return; }

        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(p.Index);
        if (from is null) { p.SendErrorMessage("[BossArena] 找不到你当前所在的世界。"); return; }
        IVirtualWorld? occ = _core.FindByOccupant(p.Index);
        if (IsArena(occ) || from.Name == ArenaServerName) { p.SendInfoMessage("[BossArena] 你已经在场地里了。用 /ba exit 返回。"); return; }
        if (occ is not null && !InteropAllows(occ)) { p.SendInfoMessage($"[BossArena] 你当前在「{occ.DisplayName}」里，且本玩法未允许从假世界直接进入；请先退出那里。"); return; }

        int tileW = from.Main.maxTilesX, tileH = from.Main.maxTilesY;
        p.SendInfoMessage("[BossArena] 正在准备 Boss 场地…");
        TShock.Log.Info($"[BossArena] {p.Name} 请求进入（{tileW}x{tileH}）");

        Task.Run(async () =>
        {
            try
            {
                IVirtualWorld world = await _core.EnsureAsync(BuildRequest(from, tileW, tileH), CancellationToken.None).ConfigureAwait(false);

                // 尺寸必须一致（跨尺寸会导致客户端空气墙）；不一致就直接拒绝，别让核心闷声失败
                ServerContext? ctx = world.Context;
                if (ctx is not null && (ctx.Main.maxTilesX != tileW || ctx.Main.maxTilesY != tileH))
                {
                    p.SendErrorMessage($"[BossArena] 场地尺寸（{ctx.Main.maxTilesX}x{ctx.Main.maxTilesY}）与你所在世界（{tileW}x{tileH}）不一致，无法进入。");
                    return;
                }

                VirtualWorldResult enter = await _core.EnterAsync(p, world, CancellationToken.None).ConfigureAwait(false);
                if (enter.Success) { p.SendSuccessMessage("[BossArena] 已进入 Boss 场地。用 /ba exit 返回，/ba reset 重置。"); }
                else { p.SendErrorMessage($"[BossArena] 进入失败：{enter.Message}"); }
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[BossArena] 进入流程异常: " + ex);
                p.SendErrorMessage("[BossArena] 准备失败: " + ex.Message);
            }
        });
    }

    private static VirtualWorldRequest BuildRequest(ServerContext from, int tileW, int tileH)
    {
        ServerContext seed = UnifiedServerCoordinator.Servers.FirstOrDefault(
            x => x.IsRunning && x.Name != ArenaServerName && !x.Name.StartsWith("Pot_", StringComparison.Ordinal)) ?? from;

        return new VirtualWorldRequest
        {
            Key = ArenaKey,
            DisplayName = "Boss 场地",
            OwnerKey = "",                  // 公共实例
            Group = "BossArena",
            Policy = new ArenaPolicy(),
            ServerName = ArenaServerName,
            WorldFileName = ArenaWorldFile,
            TilesX = tileW,
            TilesY = tileH,
            SeedWorldPath = seed.Main.worldPathName,
            Headless = true,
            IdleDestroySeconds = _config.IdleDestroySeconds,   // 由本插件的闲置检查执行（核心只当元数据）
            LogTag = "[BossArena]",

            // 加载完成：只有"刚用种子建出来的世界"才清空，避免抹掉既有存档
            OnLoaded = static (ctx, justSeeded) =>
            {
                if (justSeeded) { ctx.ClearCurrentWorld(); }
                TShock.Log.Info("[BossArena] 世界已加载");
            },

            // 每次进入前（本世界线程）：场地空着就重置，然后铺模板
            OnBeforeEnter = static (ctx, justSeeded) =>
            {
                if (justSeeded || IsEmpty(ctx))
                {
                    ctx.ClearCurrentWorld();
                    TShock.Log.Info("[BossArena] 场地已重置（空闲/新建）");
                }
                BuildTemplate(ctx);
                try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); } catch { }
            },
        };
    }

    private static void Leave(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null || _core is null) { return; }
        IVirtualWorld? occ = _core.FindByOccupant(p.Index);
        if (!IsArena(occ)) { p.SendInfoMessage(occ is null ? "[BossArena] 你不在场地里。" : $"[BossArena] 你不在场地里（在「{occ.DisplayName}」）。"); return; }

        p.SendInfoMessage("[BossArena] 正在返回…");
        Task.Run(async () =>
        {
            try
            {
                VirtualWorldResult leave = await _core.LeaveAsync(p, CancellationToken.None).ConfigureAwait(false);
                if (leave.Success) { p.SendSuccessMessage("[BossArena] 已返回原世界。"); }
                else { p.SendErrorMessage($"[BossArena] 返回失败：{leave.Message}"); }
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[BossArena] 返回失败: " + ex);
                p.SendErrorMessage("[BossArena] 返回失败: " + ex.Message);
            }
        });
    }

    private static void Reset(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is not null && !p.HasPermission("tshock.admin")) { p.SendErrorMessage("[BossArena] 你没有重置场地的权限。"); return; }
        if (_core is null) { return; }

        IVirtualWorld? w = _core.Find(ArenaKey);
        ServerContext? ctx = w?.Context;
        if (ctx is null) { p?.SendInfoMessage("[BossArena] 场地尚未加载。"); return; }

        ctx.Dispatcher.Post(() =>
        {
            try
            {
                ctx.ClearCurrentWorld();
                BuildTemplate(ctx);
                try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); } catch { }
                TShock.Log.Info($"[BossArena] 手动重置完成 by {p?.Name ?? "控制台"}");
                p?.SendSuccessMessage("[BossArena] 场地已重置。");
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[BossArena] 重置失败: " + ex);
                p?.SendErrorMessage("[BossArena] 重置失败: " + ex.Message);
            }
        });
    }

    private static void Status(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        IVirtualWorld? w = _core?.Find(ArenaKey);
        if (w is null) { Reply(p, "[BossArena] 场地尚未创建（有人 /ba 时自动创建）。"); return; }
        int live = _core is null ? 0 : _core.CountLiveOccupants(w);
        Reply(p, $"[BossArena] 场地: 已加载={w.IsLoaded} 在线玩家={live} 区域半宽={HalfWidth} 闲置卸载={(_config.IdleDestroySeconds > 0 ? _config.IdleDestroySeconds + "s" : "常驻")}");
    }

    /// <summary>回给玩家；控制台则写日志（否则控制台执行指令看不到任何输出）。</summary>
    private static void Reply(TSPlayer? p, string msg)
    {
        if (p is not null) { p.SendInfoMessage(msg); } else { TShock.Log.Info(msg); }
    }

    // ===== 模板与区域（纯玩法，全部在本世界线程上执行） =====

    /// <summary>场地里还有活着的人吗？（以世界自己的玩家表为准）</summary>
    private static bool IsEmpty(ServerContext ctx)
    {
        try
        {
            for (int i = 0; i < ctx.Main.player.Length; i++)
            {
                var pl = ctx.Main.player[i];
                if (pl is not null && pl.active) { return false; }
            }
        }
        catch { }
        return true;
    }

    /// <summary>
    /// 战斗平台模板：一层地面 + 出生点 + 两侧 2 格短墙。
    /// ★ 地面用 UTSL 公开 API <see cref="ServerContext.BuildSpawnPlatform"/>（内部走 WorldGen.PlaceTile，簿记完整）；
    ///   短墙用 Main.tile 直接写（插件可用的写法，见 GenerateMap/SeamFiller.cs:148-150）。
    /// </summary>
    private static void BuildTemplate(ServerContext ctx)
    {
        ctx.BuildSpawnPlatform(HalfWidth);      // 地面 + 出生点（区域半宽 = HalfWidth）

        int cx = ctx.Main.maxTilesX / 2;
        int cy = ctx.Main.maxTilesY / 2;
        for (int dy = 1; dy <= 2; dy++)
        {
            SetStone(ctx, cx - HalfWidth, cy - dy);
            SetStone(ctx, cx + HalfWidth, cy - dy);
        }
        TShock.Log.Info($"[BossArena] 模板已铺: 区域 x=[{cx - HalfWidth},{cx + HalfWidth}] 地面 y={cy}");
    }

    private static void SetStone(ServerContext ctx, int x, int y)
    {
        if (x < 0 || y < 0 || x >= ctx.Main.maxTilesX || y >= ctx.Main.maxTilesY) { return; }
        ref var t = ref ctx.Main.tile[x, y];
        t.active(true);
        t.type = (ushort)Terraria.ID.TileID.Stone;
    }
}
