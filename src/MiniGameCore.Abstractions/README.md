# MiniGameCore.Abstractions 契约程序集
- **作者**: 星梦
- **版本**: 契约 v1
- **类型**: 契约程序集（**不是插件**：没有 `[PluginMetadata]`，宿主不会加载它）

## 功能概述
- 玩法（Parkour 等）与核心**只共享契约、不共享实现**：核心换实现不影响玩法
- 契约版本不匹配时玩法会**明确报错**，而不是运行时才发现类型对不上
- 运行时部署位置：`plugins/MiniGameCore/lib/MiniGameCore.Abstractions.dll`

主要类型：
| 类型 | 作用 |
|------|------|
| `IMiniGameService` / `IMiniGameServiceProvider` | 服务发现与进入/离开/预热入口 |
| `IMiniGame` | 玩法钩子（建世界配置、进人、倒计时、Tick、结算） |
| `IMiniGameRoom` | 房间状态、玩家、进度、倒计时、完赛通知 |
| `IMapDefinition` | 地图元信息（尺寸/容量/时限/宽限/倒计时/世界模板） |
| `ILeaderboardStore` / `ScoreEntry` | 排行榜存储（机制）与成绩结构 |
| `MiniGameApi.Version` | 契约版本 |

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| （无） | - | 契约程序集不是插件，不注册任何指令 |

## 配置
配置文件路径：无（纯契约程序集，不读配置）

```json
{}
```

## 说明
- 依赖 `VirtualWorldCore.Abstractions`（`IVirtualWorld` / `VirtualWorldRequest` 用于建世界）
- 只做**增补式**演进：破坏性变更必须提升 `MiniGameApi.Version`

## 插件版本
### v1
- 首版：房间/地图/玩法/服务/排行榜五组契约

## 反馈
- 共同维护的插件库：https://github.com/Zykor-Club/UTSLServerPlugin