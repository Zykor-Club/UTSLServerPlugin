using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TShockAPI;
using UnifierTSL.Plugins;

namespace UTSL.VirtualWorld;

/// <summary>
/// 假世界核心（服务插件）。玩法插件在 BeforeGlobalInitialize 里按类型拿到本实例，读 <see cref="Api"/>。
/// ★ 服务对象在【构造函数】里建好 —— 构造发生在宿主加载期，早于所有 BeforeGlobalInitialize 调用
///   （见 DotnetPluginHost.Initialize.cs:22-33），因此取用时不需要关心初始化顺序。
/// </summary>
[PluginMetadata("VirtualWorldCore", "0.2.0", "星梦",
    "假世界核心：实例注册表 / 生命周期 / 玩家进出状态一致性（供尘歌壶、PvP、Boss 场地等玩法复用）")]
public sealed class VirtualWorldCorePlugin : BasePlugin, IVirtualWorldCoreProvider
{
    private static VirtualWorldCorePlugin? instance;
    private readonly VirtualWorldService service = new();
    private static VirtualWorldCoreConfig _config = new();
    private static string _configPath = "";

    /// <summary>宿主加载期即可用；其它插件按类型取用（推荐走 BeforeGlobalInitialize 的容器列表）。</summary>
    public static VirtualWorldCorePlugin? Instance => instance;

    public IVirtualWorldCore Api => service;

    /// <summary>
    /// 区块建筑模板（.utpl）在真实 ServerContext 上的端到端自检：
    /// 建隔离世界 → 铺内容 → SaveRegionAsync → 清空 → ApplyTemplateAsync → 逐格/逐实体比对。
    /// 结果通过 <paramref name="log"/> 逐行回报（调用方是 _utplverify 之类的测试插件）。
    /// ★ 它会在服务端建/改世界，所以**默认关闭**：必须显式设环境变量 UTSL_UTPL_SELFTEST=1 才执行
    ///   （与调用方 UtplVerify 插件用的是同一个变量名，避免"设了却静默跳过"）。
    /// </summary>
    public Task<string> RunUtplSelfTestAsync(string seedWorldPath, Action<string> log, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("UTSL_UTPL_SELFTEST") != "1")
        {
            log("已跳过 .utpl 自检：需要环境变量 UTSL_UTPL_SELFTEST=1（它会建/改服务端世界）");
            return Task.FromResult("skipped");
        }
        return UtplSelfTest.RunAsync(service, seedWorldPath, log, ct);
    }

    /// <summary>/vwc stats|reload（权限 virtualworldcore.admin）。</summary>
    private void WvcCommand(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "stats";
        switch (sub)
        {
            case "reload":
            {
                _config = VirtualWorldCoreConfig.Load(_configPath);
                VirtualWorldService.Config = _config;
                VirtualWorldService.DiagnosticsEnabled = _config.Diagnostics;
                Reply(args, $"[VirtualWorldCore] 已重载：模板上限 {_config.TemplateMaxWidth}x{_config.TemplateMaxHeight}、"
                    + $"实例上限 {(_config.MaxInstances > 0 ? _config.MaxInstances.ToString() : "不限")}、"
                    + $"卸载 {_config.DestroyPostTimeoutSeconds}s/{_config.DestroyCloseTimeoutSeconds}s、诊断={(_config.Diagnostics ? "开" : "关")}");
                break;
            }
            case "stats":
            default:
            {
                VirtualWorldStats st = service.GetStats();
                Reply(args, $"[VirtualWorldCore] 实例 {st.Total}（已加载 {st.Loaded}，在线占用 {st.Occupants}）");
                foreach (VirtualWorldInstanceInfo i in st.Instances)
                {
                    Reply(args, $"  {i.Key} · {i.DisplayName} {i.TilesX}x{i.TilesY} 占用 {i.Occupants} 闲置 {i.IdleSeconds:F0}s 文件 {i.WorldFileBytes / 1024 / 1024}MB{(i.Loaded ? "" : "（未加载）")}");
                }
                break;
            }
        }
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); }
        else { TShock.Log.Info(text); }
    }

    /// <summary>契约版本，供玩法发现时校验。</summary>
    public int ApiVersion => VirtualWorldApi.Version;

    public VirtualWorldCorePlugin()
    {
        instance = this;
    }

    /// <summary>排在 TShock 之后、其它玩法之前（玩法默认 5）。</summary>
    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        // TShock.Log 等在 TShock 初始化完成后才可用
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock")
            {
                await initInfo.InitializationTask;
            }
        }

        try { _configPath = Path.Combine(configRegistrar.Directory, "settings.json"); } catch { _configPath = ""; }
        _config = VirtualWorldCoreConfig.Load(_configPath);
        VirtualWorldService.Config = _config;
        VirtualWorldService.DiagnosticsEnabled = _config.Diagnostics;

        Commands.ChatCommands.Add(new Command("virtualworldcore.admin", WvcCommand, "vwc"));

        TShock.Log.Info($"[VirtualWorldCore] 已加载 v0.2.0（实例上限={(_config.MaxInstances > 0 ? _config.MaxInstances.ToString() : "不限")}，模板上限={_config.TemplateMaxWidth}x{_config.TemplateMaxHeight}，卸载超时={_config.DestroyPostTimeoutSeconds}s/{_config.DestroyCloseTimeoutSeconds}s）");
    }
}
