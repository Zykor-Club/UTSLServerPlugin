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
///   GET  /world/list
///   POST /world/command?world={名称|序号|all}&cmd={指令}[&scope=console]
///        -> response 是**指令的真实输出**（能取到就返回；取不到则说明输出在上方日志流里）
///
/// 执行走宿主提供的 TShockConsoleCommand.TryExecute（内部就是 UTSL 控制台那一套：
/// activity scope + TShock 专用执行上下文），所以 V2 指令（/help、/time 等）也能匹配并拿到 CommandOutcome。
///
/// scope=world（默认）：以**该世界的控制台**身份执行（世界相关指令语义正确）。
/// scope=console：以**启动器控制台**身份执行（全局类指令）。
///
/// ★ 历史坑（已解决）：早先直接 CommandExecutor(srv, byte.MaxValue) + Commands.HandleCommand
///   只能拿到分派回显；看 CommandExecutor.SendMessage 的实现就知道，世界控制台分支输出只进该世界 logger。
///   宿主侧已补丁：世界控制台分支同时回传 RestPlayer、并镜像一份到静态 logger（落 logs\*.log）。
/// </summary>
[PluginMetadata("WorldCommandChannel", "1.0.0", "星梦", "按世界执行指令的 REST 通道（返回真实输出）")]
public sealed class WorldCommandChannelPlugin : BasePlugin
{
    public override Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        TShock.RestApi.Register(new SecureRestCommand("/world/list", WorldList, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/world/command", WorldCommand, RestPermissions.restapi));
        TShock.Log.Info("[WorldCommandChannel] 已注册 /world/list 与 /world/command（按世界执行指令，返回真实输出）");
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

    /// <summary>对指定世界（或全部世界）执行指令，并返回指令的真实输出。</summary>
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
        // scope=console：以启动器控制台身份执行（其余情况都用"该世界的控制台"身份）
        bool consoleScope = GetParam(args, "scope").Trim().Equals("console", StringComparison.OrdinalIgnoreCase);

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

        List<string> output = [];
        bool multi = targets.Count > 1;      // 只有多世界时才加 [世界] 前缀，单世界直接给内容
        // 启动器控制台口径只需要跑一次（与具体世界无关）
        IEnumerable<ServerContext?> scopeTargets = consoleScope ? [null] : targets.Cast<ServerContext?>();
        foreach (ServerContext? srv in scopeTargets)
        {
            List<string> lines = [];
            if (multi && srv is not null) { output.Add("[" + srv.Name + "]"); }
            try
            {
                // ★ 判据是"宿主是否已经执行过"（TryExecute 返回 Handled，含调度器内部的老式兜底），
                //   **不能**用"有没有拿到文本"、也**不能**用"V2 是否 Matched"：
                //   V2 指令的输出走终端活动通道（落日志），outcome 里常常是空的 ——
                //   若拿"文本为空"当信号就会把同一条指令**再执行一遍**（/save、/give 会被执行两次！实测过）。
                bool matched = TShockConsoleCommand.TryExecute(cmd, out IReadOnlyList<string> got, srv);
                foreach (string line in got)
                {
                    foreach (string part in line.Replace("\r\n", "\n").Split('\n'))
                    {
                        if (part.Trim().Length > 0) { lines.Add(part.TrimEnd()); }
                    }
                }

                // 没匹配上才退回老式路径（插件指令/老式指令走这里）：
                // 用"世界控制台身份"执行（不传 RestPlayer，语义不变），输出由宿主补丁镜像进日志。
                if (!matched)
                {
                    CommandExecutor exec = new(srv, byte.MaxValue);
                    if (!Commands.HandleCommand(exec, cmd)) { lines.Add("未知指令（没有这个命令）"); }
                    // 老式指令的输出同样在日志流里 —— 不再回"已执行，输出见日志流"这类提示（用户要求去掉）
                }
            }
            catch (Exception ex)
            {
                lines.Add("执行失败: " + ex.Message);
            }

            foreach (string line in lines) { output.Add(multi && srv is not null ? "  " + line : line); }
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "200",
            ["world"] = selector,
            ["count"] = consoleScope ? 1 : targets.Count,
            ["response"] = output,
        };
    }
}
