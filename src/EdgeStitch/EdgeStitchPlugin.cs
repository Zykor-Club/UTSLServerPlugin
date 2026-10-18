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
[PluginMetadata("EdgeStitch", "1.2.1", "星梦", "多世界边缘无缝拼接：走到世界边缘自动进入相邻世界的对应边缘")]
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
        Commands.ChatCommands.Add(new Command("", StitchCommand, "stitch"));

        foreach (string warn in _manager.Validate(includeRuntime: false))
        {
            TShock.Log.Warning("[EdgeStitch] 配置自检: " + warn);
        }

        TShock.Log.Info($"[EdgeStitch] 已加载，世界链: {string.Join(" — ", _manager.Config.WorldChain)}"
            + $"（环={_manager.Config.Loop}，边缘带={_manager.Config.EdgeBandTiles}格，冷却={_manager.Config.CooldownMs}ms）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnPostUpdate);
            UnifierApi.EventHub.Netplay.LeaveEvent.UnRegister(OnServerLeave);
            System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate.Method?.DeclaringType?.Assembly == asm);
        }
        return ValueTask.CompletedTask;
    }

    private void OnPostUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
        => _manager.OnPostUpdate(args.Content.Server);


    private void OnServerLeave(ref ReadonlyNoCancelEventArgs<LeaveEvent> args)
        => _manager.ClearPlayer(args.Content.Who);

    /// <summary>/stitch [status|reload|on|off|diag]</summary>
    private void StitchCommand(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "status";
        bool admin = args.Player is null || args.Player.HasPermission("tshock.admin");

        switch (sub)
        {
            case "reload":
                if (!admin) { Reply(args, "[EdgeStitch] 没有权限。"); return; }
                _manager.Config = EdgeStitchConfig.Load(_configPath);
                Reply(args, $"[EdgeStitch] 已重载配置（{string.Join(" — ", _manager.Config.WorldChain)}）");
                List<string> warns = _manager.Validate();
                if (warns.Count == 0) { Reply(args, "[EdgeStitch] 自检通过"); }
                else { foreach (string w in warns) { Reply(args, "[EdgeStitch] ⚠ " + w); } }
                break;
            case "on":
            case "off":
                if (!admin) { Reply(args, "[EdgeStitch] 没有权限。"); return; }
                _manager.Config.Enabled = sub == "on";
                TrySave(args);
                Reply(args, $"[EdgeStitch] 换乘已{(sub == "on" ? "开启" : "关闭")}");
                break;
            case "diag":
                if (!admin) { Reply(args, "[EdgeStitch] 没有权限。"); return; }
                _manager.Config.DiagnosticLog = !_manager.Config.DiagnosticLog;
                TrySave(args);
                Reply(args, $"[EdgeStitch] 诊断日志已{(_manager.Config.DiagnosticLog ? "开启" : "关闭")}");
                break;
            case "status":
            default:
                foreach (string line in _manager.Describe()) { Reply(args, "[EdgeStitch] " + line); }
                break;
        }
    }

    private void TrySave(CommandArgs args)
    {
        try { _manager.Config.Save(_configPath); }
        catch (Exception ex)
        {
            TShock.Log.Warning("[EdgeStitch] 保存配置失败: " + ex.Message);
            Reply(args, "[EdgeStitch] 保存配置失败（本次仅内存生效）: " + ex.Message);
        }
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); }
        else { TShock.Log.Info(text); }
    }
}
