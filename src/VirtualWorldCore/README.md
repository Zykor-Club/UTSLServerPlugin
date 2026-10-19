# VirtualWorldCore 假世界核心
- **作者**: 星梦
- **版本**: v0.2.0
- **类型**: UTSL 核心模块（`[CoreModule]`）

## 功能概述
- 把"假世界基础设施"从具体玩法里抽出来，供**尘歌壶 / Boss 场地 / 将来的 PvP、副本**复用
- **实例注册表**：按稳定 key 管理实例
- **生命周期**：按需创建/加载、安全卸载（带守卫，绝不踩掉有人的世界）
- **玩家进出**：核心负责转移 + 状态一致性 + 记账，玩法不用自己维护映射
- **返回栈**：记住玩家从哪来，离开时原路返回，支持嵌套
- **模板基线**：同尺寸实例首次初始化后缓存为基线，之后新实例直接复制（省掉复制大世界 + 全图清空）
- **可否决事件**：`Transferring` / `Transferred` / `Destroying`，任何插件都能挂

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/vwc stats` | `virtualworldcore.admin` | 实例总览：数量/加载/占用；每个实例一行（key、尺寸、占用、闲置、存档 MB） |
| `/vwc reload` | `virtualworldcore.admin` | 重载 `settings.json`（超时/上限/诊断立即生效） |

## 配置
配置文件路径：`config/VirtualWorldCore/settings.json`

```json
{
  "模板_最大宽": 1500,
  "模板_最大高": 1300,
  "模板_最大文件MB": 64,
  "实例_最多同时存在": 0,
  "卸载_等待世界线程秒": 10,
  "卸载_关闭世界秒": 15,
  "加载_等待秒": 30,
  "诊断日志": true
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 模板_最大宽 | 整数 | 1500 | 区块模板最大宽；`SaveRegionAsync` / `ApplyTemplateAsync` 超限直接拒绝 |
| 模板_最大高 | 整数 | 1300 | 区块模板最大高 |
| 模板_最大文件MB | 整数 | 64 | 模板文件大小上限（防御坏文件） |
| 实例_最多同时存在 | 整数 | 0 | 所有玩法实例总数上限；0 = 不限（各玩法自管） |
| 卸载_等待世界线程秒 | 整数 | 10 | 卸载时等待世界线程存档/清理 |
| 卸载_关闭世界秒 | 整数 | 15 | 卸载时等待 `ctx.Close()`（世界线程睡眠时接近该值） |
| 加载_等待秒 | 整数 | 30 | 加载世界时等待其跑起来 |
| 诊断日志 | 布尔值 | true | 核心诊断日志总开关 |

## 说明
- 核心只解释**生命周期与占用**；玩法策略（准入门槛、世界名、模板、清理规则）全部由 `VirtualWorldRequest` / `IVirtualWorldPolicy` 传入
- 卸载前有三重守卫：世界玩家表仍有 active 玩家 / 仍在返回栈 / 玩法策略否决 → 都会拒绝销毁
- 世界级状态同步不在核心范围（见 `WorldChainSync`）
- `/vwc stats` 走可选接口 `IVirtualWorldStatsProvider`：核心实现它，玩法不受影响，契约版本不变
- 配置改动可用 `/vwc reload` 热更，不必重启

## 玩法接入方式
1. 引用 `VirtualWorldCore.Abstractions`；
2. 程序集标 `[assembly: RequiresCoreModule("VirtualWorldCore")]`；
3. `BeforeGlobalInitialize` 里从 `IVirtualWorldCoreProvider` 取 `Api`，并校验 `VirtualWorldApi.Version`；
4. 用 `VirtualWorldRequest` + `EnsureAsync` 建世界，`EnterAsync` / `LeaveAsync` 进出。

## 部署布局
```
plugins/VirtualWorldCore/
    VirtualWorldCore.dll
    lib/VirtualWorldCore.Abstractions.dll
    <玩法插件>.dll          # 例如 MyPotWorld.dll / BossArena.dll
```
玩法与核心放同一模块目录，是为了让宿主载入**同一个 ALC**，保证契约类型身份唯一。

## 插件版本
### v0.2.0
- 新增 `/vwc stats|reload`（权限 `virtualworldcore.admin`）
- 新增 `settings.json`：模板尺寸/文件上限、实例总数上限、卸载/加载超时、诊断开关
- 新增可选接口 `IVirtualWorldStatsProvider`（不 bump 契约版本）

### v0.1.0
- 契约/实现分离（Abstractions）、稳定服务发现 + 版本校验
- 契约 v5：可否决事件、统一 Result、互通开关、模板基线

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
