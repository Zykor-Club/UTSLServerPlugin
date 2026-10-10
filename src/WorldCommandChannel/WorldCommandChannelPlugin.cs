using System.Collections.Immutable;
using Rests;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace WorldCommandChannel;

/// <summary>
/// WorldCommandChannel —— 按世界执行指令的 REST 通道（供外部管理器使用）。
///
/// 背景：UTSL 是【单进程多世界】，TShock 自带的 /v3/server/rawcmd 只能落到默认世界，
/// 外部管理器无法对指定世界发指令。本插件补上这个能力：
///
///   GET  /world/list
///        -> 世界列表（名称、尺寸、在线人数、是否运行）
///   POST /world/command?world={名称|序号|all}&cmd={指令}
///        -> 对指定世界执行指令；world 省略或 all 表示【所有世界】
///        -> 返回 {"status":"200","response":[...]}（与 TShock rawcmd 同格式，便于管理器复用）
///
/// 执行语义与在控制台敲完全等价：CommandExecutor(SourceServer=目标世界, UserId=byte.MaxValue)
/// 即 UTSL 的「服务器权限 + 指定世界」。
/// </summary>
[PluginMetadata("WorldCommandChannel", "1.0.0", "星梦", "按世界执行指令的 REST 通道（供外部管理器使用）")]
public sealed class WorldCommandChannelPlugin : BasePlugin
{
    public override Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        TShock.RestApi.Register(new SecureRestCommand("/world/list", WorldList, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/world/command", WorldCommand, RestPermissions.restapi));
        TShock.Log.Info("[WorldCommandChannel] 已注册 /world/list 与 /world/command（按世界执行指令）");
        return Task.CompletedTask;
    }

    /// <summary>安全读取查询参数（EscapedParameterCollection 的索引器缺键会抛）。</summary>
    private static string GetParam(RestRequestArgs args, string key)
    {
        try
        {
            string? v = args.Parameters[key];
            return v ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>世界列表。</summary>
    private static object WorldList(RestRequestArgs args)
    {
        List<object> list = [];
        int index = 0;
        foreach (ServerContext srv in UnifiedServerCoordinator.Servers)
        {
            index++;
            list.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["name"] = srv.Name,
                ["running"] = srv.IsRunning,
                ["width"] = srv.IsRunning ? srv.Main.maxTilesX : 0,
                ["height"] = srv.IsRunning ? srv.Main.maxTilesY : 0,
            });
        }
        return new Dictionary<string, object?>
        {
            ["status"] = "200",
            ["count"] = list.Count,
            ["worlds"] = list,
        };
    }

    /// <summary>对指定世界（或全部世界）执行指令。</summary>
    private static object WorldCommand(RestRequestArgs args)
    {
        string selector = GetParam(args, "world");
        if (string.IsNullOrWhiteSpace(selector)) { selector = "all"; }
        string cmd = GetParam(args, "cmd").Trim();
        if (cmd.Length == 0)
        {
            return new Dictionary<string, object?> { ["status"] = "400", ["error"] = "缺少 cmd 参数" };
        }
        if (!cmd.StartsWith('/')) { cmd = "/" + cmd; }

        List<ServerContext> all = [.. UnifiedServerCoordinator.Servers.Where(s => s.IsRunning)];
        List<ServerContext> targets;
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets = all;
        }
        else if (int.TryParse(selector, out int idx))
        {
            targets = idx >= 1 && idx <= all.Count ? [all[idx - 1]] : [];
        }
        else
        {
            ServerContext? hit = all.FirstOrDefault(s => string.Equals(s.Name, selector, StringComparison.OrdinalIgnoreCase));
            targets = hit is null ? [] : [hit];
        }

        if (targets.Count == 0)
        {
            return new Dictionary<string, object?> { ["status"] = "404", ["error"] = "找不到世界: " + selector };
        }

        Group group = new SuperAdminGroup();
        TSRestPlayer restPlayer = new(args.TokenData.Username, group);
        List<string> output = [];
        foreach (ServerContext srv in targets)
        {
            try
            {
                CommandExecutor executor = new(srv, byte.MaxValue, restPlayer);
                Commands.HandleCommand(executor, cmd);
                output.Add($"[{srv.Name}] -> {cmd}");
            }
            catch (Exception ex)
            {
                output.Add($"[{srv.Name}] 失败: {ex.Message}");
            }
        }
        return new Dictionary<string, object?>
        {
            ["status"] = "200",
            ["world"] = selector,
            ["count"] = targets.Count,
            ["response"] = output,
        };
    }
}
