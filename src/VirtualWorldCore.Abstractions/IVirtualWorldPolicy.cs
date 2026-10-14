using TShockAPI;

namespace UTSL.VirtualWorld;

/// <summary>玩法策略：核心在关键节点回调；返回 false 表示否决。</summary>
public interface IVirtualWorldPolicy
{
    bool CanJoin(TSPlayer player, IVirtualWorld world);

    /// <summary>例如"还有人预约"时否决回收。</summary>
    bool CanDestroy(IVirtualWorld world);

    void OnLoaded(IVirtualWorld world);
    void OnPlayerEntered(TSPlayer player, IVirtualWorld world);
    void OnPlayerLeft(TSPlayer player, IVirtualWorld world);
}
