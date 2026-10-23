using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;
using UnifierTSL.PluginService;
using UnifierTSL.Servers;
using UTSL.VirtualWorld;

namespace UTSL.MiniGame;

/// <summary>
/// 小游戏核心（服务插件）：房间状态机 / 大厅与排队 / 计时 / 结算 / 排行榜存储。
/// 玩法是**子插件**，在 BeforeGlobalInitialize 里按契约接口发现本服务并 RegisterGame。
/// ★ 构造期就建好服务对象（宿主在加载期构造，早于所有 BeforeGlobalInitialize）。
/// </summary>
[PluginMetadata("MiniGameCore", "0.1.0", "星梦",
    "小游戏核心：房间生命周期 / 大厅排队 / 计时与结算 / 排行榜存储（玩法以子插件注册，如 Parkour）")]
public sealed class MiniGameCorePlugin : BasePlugin, IMiniGameServiceProvider
{
    private static MiniGameCorePlugin? instance;
    private readonly MiniGameService service = new();
    private static MiniGameCoreConfig _config = new();
    private static string _configPath = "";
    private static string coreStatus = "未执行服务发现";

    public static MiniGameCorePlugin? Instance => instance;

    public IMiniGameService Api => service;

    public int ApiVersion => MiniGameApi.Version;

    public MiniGameCorePlugin() { instance = this; }

    /// <summary>排在 TShock 与 VirtualWorldCore 之后（VirtualWorldCore 是 TShock.Order + 1）。</summary>
    public override int InitializationOrder => TShock.Order + 2;

    /// <summary>服务发现：找 VirtualWorldCore（只认契约接口）。</summary>
    public override void BeforeGlobalInitialize(ImmutableArray<IPluginContainer> plugins)
    {
        try
        {
            foreach (IPluginContainer c in plugins)
            {
                if (c.Plugin is not IVirtualWorldCoreProvider provider) { continue; }
                if (provider.ApiVersion != VirtualWorldApi.Version)
                {
                    coreStatus = $"★假世界核心契约不匹配（核心 v{provider.ApiVersion}，期望 v{VirtualWorldApi.Version}）";
                    return;
                }
                service.AttachVirtualWorldCore(provider.Api);
                coreStatus = $"已取得 VirtualWorldCore（契约 v{VirtualWorldApi.Version}）";
                return;
            }
            coreStatus = "★没找到 VirtualWorldCore（假世界核心未加载）";
        }
        catch (Exception ex)
        {
            coreStatus = "服务发现异常: " + ex.GetType().Name;
        }
    }

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock") { await initInfo.InitializationTask.ConfigureAwait(false); }
        }

        try { _configPath = Path.Combine(configRegistrar.Directory, "settings.json"); } catch { _configPath = ""; }
        _config = MiniGameCoreConfig.Load(_configPath);
        MiniGameService.Config = _config;

        UnifierApi.EventHub.Game.PreUpdate.Register(OnPreUpdate, HandlerPriority.Normal);
        UnifierApi.EventHub.Netplay.LeaveEvent.Register(OnPlayerLeave, HandlerPriority.Normal);
        Commands.ChatCommands.Add(new Command("minigame.admin", MgCommand, "mg"));

        TShock.Log.Info($"[MiniGameCore] 服务发现: {coreStatus}");
        TShock.Log.Info($"[MiniGameCore] 已加载 v0.1.0（契约 v{MiniGameApi.Version}，房间上限={(_config.MaxRooms > 0 ? _config.MaxRooms.ToString() : "不限")}，"
            + $"等候厅空置 {_config.LobbyUnloadSeconds}s 卸载，宽限 {_config.GraceSeconds}s，赛季 {_config.Season}）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        try { UnifierApi.EventHub.Game.PreUpdate.UnRegister(OnPreUpdate); } catch { }
        try { UnifierApi.EventHub.Netplay.LeaveEvent.UnRegister(OnPlayerLeave); } catch { }
        return ValueTask.CompletedTask;
    }

    /// <summary>每个假世界都有自己的更新循环：这里按"事件来自哪个 ServerContext"分发到对应房间。</summary>
    private static void OnPreUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        try { instance?.service.OnServerFrame(args.Content.Server); }
        catch { }
    }

    /// <summary>玩家离开服务器（掉线/踢出）：把房间里的记录清掉，避免空房一直挂到时限。</summary>
    private static void OnPlayerLeave(ref ReadonlyNoCancelEventArgs<LeaveEvent> args)
    {
        try { instance?.service.OnPlayerGone(args.Content.Who); }
        catch { }
    }

    private void MgCommand(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "rooms";
        switch (sub)
        {
            case "rooms":
            case "list":
            {
                IReadOnlyList<IMiniGameRoom> rooms = service.Rooms;
                Reply(args, $"[MiniGameCore] 房间 {rooms.Count} 个，玩法 {service.Games.Count} 个");
                foreach (IMiniGameRoom r in rooms)
                {
                    Reply(args, $"  {r.RoomId} · {r.GameId}/{r.Map.Id} · {r.Mode} · {r.State} · {r.Players.Count}/{r.Map.Capacity} · tick={r.TickCount}");
                }
                foreach (IMiniGame g in service.Games)
                {
                    Reply(args, $"  玩法 {g.Id}：{string.Join(", ", g.Maps.Select(m => $"{m.Id}({m.TilesX}x{m.TilesY},cap={m.Capacity})"))}");
                }
                break;
            }
            case "reload":
            {
                _config = MiniGameCoreConfig.Load(_configPath);
                MiniGameService.Config = _config;
                Reply(args, $"[MiniGameCore] 已重载：房间上限={(_config.MaxRooms > 0 ? _config.MaxRooms.ToString() : "不限")}，等候厅空置 {_config.LobbyUnloadSeconds}s，宽限 {_config.GraceSeconds}s");
                break;
            }
            default:
                Reply(args, "用法: /mg rooms | /mg reload");
                break;
        }
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); }
        else { TShock.Log.Info(text); }
    }
}
