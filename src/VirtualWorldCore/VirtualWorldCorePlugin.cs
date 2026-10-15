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
[PluginMetadata("VirtualWorldCore", "0.1.0", "星梦",
    "假世界核心：实例注册表 / 生命周期 / 玩家进出状态一致性（供尘歌壶、PvP、Boss 场地等玩法复用）")]
public sealed class VirtualWorldCorePlugin : BasePlugin, IVirtualWorldCoreProvider
{
    private static VirtualWorldCorePlugin? instance;
    private readonly VirtualWorldService service = new();

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

        TShock.Log.Info($"[VirtualWorldCore] 已加载 v0.1.0（注册表 + 生命周期 + 玩家进出封装就绪；等待玩法接入）");
    }
}
