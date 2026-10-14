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
[PluginMetadata("BossArena", "0.1.0", "星梦", "Boss 场地：公共假世界 + 战斗平台模板 + 区域重置")]
public sealed class BossArenaPlugin : BasePlugin
{
    private const string ArenaKey = "arena:boss";
    private const string ArenaServerName = "BossArena";
    private const string ArenaWorldFile = "BossArena";
    private const int HalfWidth = 60;          // 战斗区域半宽（格）；区域 = [cx-60, cx+60]

    private static IVirtualWorldCore? _core;
    private static bool _apiVersionMismatch;

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

        Commands.ChatCommands.Add(new Command("", Command, "ba"));
        TShock.Log.Info(_core is not null
            ? $"[BossArena] 已加载 v0.1.0（服务发现: 已取得 VirtualWorldCore，契约 v{VirtualWorldApi.Version}）"
            : _apiVersionMismatch
                ? $"[BossArena] 已加载 v0.1.0（★契约版本不匹配，期望 v{VirtualWorldApi.Version}，功能已禁用）"
                : "[BossArena] 已加载 v0.1.0（★未取得 VirtualWorldCore）");
    }

    /// <summary>该实例是不是【本玩法的 Boss 场地】。</summary>
    private static bool IsArena(IVirtualWorld? w) => w is not null && w.Key == ArenaKey;

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
        if (occ is not null) { p.SendInfoMessage($"[BossArena] 你当前在「{occ.DisplayName}」里，请先退出那里再进 Boss 场地。"); return; }

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

                bool ok = await _core.EnterAsync(p, world, CancellationToken.None).ConfigureAwait(false);
                if (ok) { p.SendSuccessMessage("[BossArena] 已进入 Boss 场地。用 /ba exit 返回，/ba reset 重置。"); }
                else { p.SendErrorMessage("[BossArena] 进入失败：世界转移被拒绝，请稍后再试。"); }
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
            ServerName = ArenaServerName,
            WorldFileName = ArenaWorldFile,
            TilesX = tileW,
            TilesY = tileH,
            SeedWorldPath = seed.Main.worldPathName,
            Headless = true,
            IdleDestroySeconds = 0,         // 常驻，不回收（本玩法不需要回收）
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
                bool ok = await _core.LeaveAsync(p, CancellationToken.None).ConfigureAwait(false);
                if (ok) { p.SendSuccessMessage("[BossArena] 已返回原世界。"); }
                else { p.SendErrorMessage("[BossArena] 返回失败：世界转移被拒绝，请稍后再试。"); }
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
        if (w is null) { p?.SendInfoMessage("[BossArena] 场地尚未创建（有人 /ba 时自动创建）。"); return; }
        int live = _core is null ? 0 : _core.CountLiveOccupants(w);
        p?.SendInfoMessage($"[BossArena] 场地: 已加载={w.IsLoaded} 在线玩家={live} 区域半宽={HalfWidth}");
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
