using System.Collections.Immutable;
using EdgeStitch.Config;
using EdgeStitch.Core;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace EdgeStitch;

/// <summary>
/// EdgeStitch —— 多世界边缘无缝拼接（UTSL 版）。
///
/// 让玩家走到世界边缘时自动进入相邻世界的对应边缘，从而把多个同尺寸世界
/// 「拼」成一张连续的大地图。因为 UTSL 是单进程多世界，换乘是一次真实的
/// 服务器切换，但玩家侧几乎无感（实测闪屏可忽略，背包/血量/buff 均保留）。
///
/// 依赖的 UTSL 能力：
///   - UnifiedServerCoordinator.TransferPlayerToServer(byte plr, ServerContext to)
///     （plr 是客户端索引；必须在源世界的 update 线程上调用）
/// </summary>
[PluginMetadata("EdgeStitch", "1.0.0", "星梦", "多世界边缘无缝拼接：走到世界边缘自动进入相邻世界的对应边缘")]
public sealed class EdgeStitchPlugin : BasePlugin
{
    private readonly EdgeTransferManager _manager = new();
    private string _configPath = "";

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

        _configPath = Path.Combine(configRegistrar.Directory, "EdgeStitch.json");
        _manager.Config = EdgeStitchConfig.Load(_configPath);

        UnifierApi.EventHub.Game.PostUpdate.Register(OnPostUpdate, HandlerPriority.Normal);
        UnifierApi.EventHub.Netplay.LeaveEvent.Register(OnServerLeave, HandlerPriority.Normal);

        TShock.Log.Info($"[EdgeStitch] 已加载，世界链: {string.Join(" — ", _manager.Config.WorldChain)}"
            + $"（环={_manager.Config.Loop}，边缘带={_manager.Config.EdgeBandTiles}格，冷却={_manager.Config.CooldownMs}ms）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnPostUpdate);
            UnifierApi.EventHub.Netplay.LeaveEvent.UnRegister(OnServerLeave);
        }
        return ValueTask.CompletedTask;
    }

    private void OnPostUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
        => _manager.OnPostUpdate(args.Content.Server);


    private void OnServerLeave(ref ReadonlyNoCancelEventArgs<LeaveEvent> args)
        => _manager.ClearPlayer(args.Content.Who);
}
