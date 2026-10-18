# VirtualWorldCore.Abstractions 契约程序集
- **作者**: 星梦
- **版本**: 契约 v7
- **类型**: 契约程序集（**不是插件**：没有 `[PluginMetadata]`，宿主不会加载它）

## 功能概述
- 玩法与核心**只共享契约、不共享实现**：核心换实现不影响玩法
- 契约版本不匹配时玩法会**明确报错**，而不是运行时才发现类型对不上
- 运行时部署位置：`plugins/VirtualWorldCore/lib/VirtualWorldCore.Abstractions.dll`

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| （无） | - | 契约程序集不是插件，不注册任何指令 |

## 配置
配置文件路径：无（纯契约程序集，不读配置）

```json
{}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| （无） | - | - | 契约不包含配置 |

## 说明
- **内容清单**

| 文件 | 内容 |
|------|------|
| IVirtualWorldCore.cs | 核心服务接口（注册表 / 生命周期 / 进出 / 记账 / 事件） |
| IVirtualWorld.cs | 单个假世界实例（Key / Group / Context / Occupants） |
| VirtualWorldRequest.cs | 创建请求（Key / DisplayName / 尺寸 / 种子 / Group / TemplateKey / 回调） |
| VirtualWorldResult.cs | 统一返回（Success / Reason / Message）+ 失败原因枚举 |
| VirtualWorldEvents.cs | 可否决事件参数（TransferArgs / DestroyArgs） |
| IVirtualWorldPolicy.cs | 玩法自定的准入门槛 |
| IVirtualWorldCoreProvider.cs | 服务发现入口（Api / ApiVersion） |
| VirtualWorldApi.cs | 契约版本号（当前 7） |

- 部署时必须放在 `<模块名>/lib/` 下，宿主靠它解析依赖（见 ModuleLoadContext）
- 玩法工程只引用本程序集（`Private=false`），不随包发布实现

## 插件版本
### 契约 v7
- 区块建筑模板：SaveRegionAsync / ApplyTemplateAsync / ListRegionTemplates（.utpl）

### 契约 v6
- 整世界模板基线（ListTemplates / SaveTemplateAsync）

### 契约 v5
- 可否决事件、统一 Result、互通开关、模板基线

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
