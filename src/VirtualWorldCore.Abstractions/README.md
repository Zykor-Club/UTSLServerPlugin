# VirtualWorldCore.Abstractions · 契约程序集

> ⚠️ 这**不是插件**：没有 `[PluginMetadata]`，宿主不会把它当插件加载。
> 运行时部署在 `plugins/VirtualWorldCore/lib/VirtualWorldCore.Abstractions.dll`。

## 里面有什么

| 文件 | 内容 |
|:--|:--|
| `IVirtualWorldCore.cs` | 核心服务接口（注册表 / 生命周期 / 进出 / 记账 / 事件） |
| `IVirtualWorld.cs` | 单个假世界实例（Key / Group / Context / Occupants） |
| `VirtualWorldRequest.cs` | 创建请求（Key / DisplayName / 尺寸 / 种子 / Group / TemplateKey） |
| `VirtualWorldResult.cs` | 统一返回（Success / Reason / Message）+ 失败原因枚举 |
| `VirtualWorldEvents.cs` | 可否决事件参数（`VirtualWorldTransferArgs` / `VirtualWorldDestroyArgs`） |
| `IVirtualWorldPolicy.cs` | 玩法自定的准入门槛 |
| `IVirtualWorldCoreProvider.cs` | 服务发现入口（`Api` / `ApiVersion`） |
| `VirtualWorldApi.cs` | 契约版本号 |

## 为什么单独一个程序集

玩法与核心**只共享契约、不共享实现**：核心换实现不影响玩法；契约版本不匹配时玩法会**明确报错**，
而不是等到运行时才发现类型对不上。
