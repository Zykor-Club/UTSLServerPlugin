using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TShockAPI;

namespace UTSL.VirtualWorld;

/// <summary>
/// 核心服务：实例生命周期 + 玩家进出 + 注册表 + 回收 + 观测。
/// 玩法通过 BeforeGlobalInitialize 拿到 VirtualWorldCorePlugin.Api（见 50 号草案第 2 节）。
/// </summary>
public interface IVirtualWorldCore
{
    /// <summary>当前已登记的全部实例（快照）。</summary>
    IReadOnlyList<IVirtualWorld> Worlds { get; }

    /// <summary>按稳定 key 查找实例；不存在返回 null。</summary>
    IVirtualWorld? Find(string key);

    /// <summary>按拥有者 key 查找实例；不存在返回 null。</summary>
    IVirtualWorld? FindByOwner(string ownerKey);

    /// <summary>确保实例存在并已加载（必要时创建世界、加载、执行 OnLoaded）。</summary>
    Task<IVirtualWorld> EnsureAsync(VirtualWorldRequest request, CancellationToken ct);

    /// <summary>把玩家送进实例（核心负责：转移 + 全部状态一致性 + 记账 + 诊断）。</summary>
    Task<VirtualWorldResult> EnterAsync(TSPlayer player, IVirtualWorld world, CancellationToken ct);

    /// <summary>把玩家送回来源世界（核心负责解绑与清理）。</summary>
    Task<VirtualWorldResult> LeaveAsync(TSPlayer player, CancellationToken ct);

    /// <summary>
    /// 卸载实例（核心负责：确认无 active 玩家 → 清悬空条目 → Close → RemoveServer）。
    /// 失败时 <see cref="VirtualWorldResult.Reason"/> 说明【本轮为什么放弃销毁】。
    /// </summary>
    Task<VirtualWorldResult> DestroyAsync(IVirtualWorld world, bool save, CancellationToken ct);

    // ===== 记账查询与看护（玩法侧需要；对应改造前 PocketWorld 的 Inside / Janitor / SelfHeal / StateCheck）=====

    /// <summary>玩家当前所在的实例（按占用记录）；不在任何实例返回 null。</summary>
    IVirtualWorld? FindByOccupant(int playerIndex);

    /// <summary>
    /// 该实例【真实在线且确实路由到该世界】的玩家数 —— 以 TShock 在线状态 + 路由指针为准（铁律②），
    /// 不用我们自己维护的映射。用途：/cg status 显示、闲置回收判定。
    /// </summary>
    int CountLiveOccupants(IVirtualWorld world);

    /// <summary>
    /// 自愈（只在"进入"路径调用）：记录与玩家真实所在世界不符时清理该玩家的记录。
    /// 不要在对"状态看起来过期"的在线玩家做别的清理（铁律③）。
    /// </summary>
    void SelfHeal(int playerIndex);

    /// <summary>对账：清理【已离线】玩家的残留记录。在线玩家一律不动（铁律③）。</summary>
    void Reconcile();

    /// <summary>状态一致性诊断：只在发现不一致时输出日志。</summary>
    void StateCheck();

    /// <summary>玩家登出：摘占用 + 清返回路径。返回登出时所在的实例（若有）。</summary>
    IVirtualWorld? NotifyPlayerGone(int playerIndex);

    event Action<IVirtualWorld>? WorldLoaded;
    event Action<IVirtualWorld>? WorldDestroyed;
    event Action<TSPlayer, IVirtualWorld>? PlayerEntered;
    event Action<TSPlayer, IVirtualWorld>? PlayerLeft;
}
