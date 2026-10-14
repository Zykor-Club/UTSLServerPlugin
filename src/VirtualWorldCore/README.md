# VirtualWorldCore · 假世界核心

> 分类：UTSL 核心模块（`[CoreModule]`）
> 契约版本见 `VirtualWorldApi.Version`（当前 v5）

## 它做什么

把"假世界基础设施"从具体玩法里抽出来，让 **尘歌壶 / Boss 场地 / 将来的 PvP、副本** 复用同一套能力：

- **实例注册表**：按稳定 key 管理实例（`Worlds` / `Find` / `FindByOwner`）；
- **生命周期**：`EnsureAsync`（按需创建/加载）、`DestroyAsync`（安全卸载，带守卫）；
- **玩家进出**：`EnterAsync` / `LeaveAsync`，核心负责转移 + 状态一致性 + 记账；
- **返回栈**：记住玩家从哪来，离开时原路返回，支持嵌套；
- **模板基线**：同尺寸实例首次初始化后缓存为基线，之后新实例直接复制基线（省掉"复制大世界 + 全图清空"）；
- **观测与自愈**：`CountLiveOccupants` / `Reconcile` / `StateCheck`；
- **可否决事件**：`Transferring` / `Transferred` / `Destroying`，任何插件都能挂，不必成为玩法。

## 玩法怎么接入

1. 引用 `VirtualWorldCore.Abstractions`；
2. 程序集标 `[assembly: RequiresCoreModule("VirtualWorldCore")]`；
3. `BeforeGlobalInitialize` 里从 `IVirtualWorldCoreProvider` 取 `Api`，并校验 `VirtualWorldApi.Version`；
4. 用 `VirtualWorldRequest` + `EnsureAsync` 建世界，用 `EnterAsync` / `LeaveAsync` 进出。

## 部署布局

```
plugins/VirtualWorldCore/
    VirtualWorldCore.dll
    lib/VirtualWorldCore.Abstractions.dll
    <玩法插件>.dll          # 例如 MyPotWorld.dll / BossArena.dll
```

玩法插件与核心放在同一个模块目录下，是为了让宿主把它们载入**同一个 ALC**，
从而保证契约类型的**身份唯一**（否则 `IVirtualWorldCore` 会变成两个不同的类型）。
