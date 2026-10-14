# VirtualWorldCore.Abstractions 契约程序集
- **作者**: 星梦
- **版本**: 契约 v5
- **类型**: 契约程序集（**不是插件**：没有 `[PluginMetadata]`，宿主不会加载它）

## 功能概述
- 玩法与核心**只共享契约、不共享实现**：核心换实现不影响玩法
- 契约版本不匹配时玩法会**明确报错**，而不是运行时才发现类型对不上
- 运行时部署位置：`plugins/VirtualWorldCore/lib/VirtualWorldCore.Abstractions.dll`

## 内容清单
| 文件 | 内容 |
|------|------|
| IVirtualWorldCore.cs | 核心服务接口（注册表 / 生命周期 / 进出 / 记账 / 事件） |
| IVirtualWorld.cs | 单个假世界实例（Key / Group / Context / Occupants） |
| VirtualWorldRequest.cs | 创建请求（Key / DisplayName / 尺寸 / 种子 / Group / TemplateKey） |
| VirtualWorldResult.cs | 统一返回（Success / Reason / Message）+ 失败原因枚举 |
| VirtualWorldEvents.cs | 可否决事件参数（TransferArgs / DestroyArgs） |
| IVirtualWorldPolicy.cs | 玩法自定的准入门槛 |
| IVirtualWorldCoreProvider.cs | 服务发现入口（Api / ApiVersion） |
| VirtualWorldApi.cs | 契约版本号（当前 5） |

## 配置
无（纯契约程序集，不读配置）

## 插件版本
### 契约 v5
- 新增 `VirtualWorldRequest.TemplateKey`（模板基线加速）

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
