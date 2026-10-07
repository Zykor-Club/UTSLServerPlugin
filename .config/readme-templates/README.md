<div align="center">

# Zykor-Club UTSL Server Plugin

UnifierTSL 插件收集仓库

</div>

## 前言
- 这是一个 UnifierTSL（UTSL）插件收集仓库，由 ZykorClub 成员维护
- 收录从 TShock 插件移植适配到 UTSL 的插件
- 插件持续更新中

## 插件列表

<!--{PluginList,zh-CN}-->

## 使用说明
1. 将 `.dll` 文件放入 UTSL 服务器的 `plugins` 文件夹
   - ⚠️ **只放插件自己的 dll**，不要复制 UnifierTSL.dll / TShockAPI.dll，宿主已经有了
2. 重启 UTSL 服务器
3. 查看对应插件的 README 了解具体用法

> 传统 TShock 插件（`TerrariaPlugin` + `[ApiVersion]`）**不能**直接放进 UTSL 使用，
> 必须按本仓库的方式移植改写（见各插件 README 与下方说明）。

## 从 TShock 插件移植到 UTSL

| 项 | TShock | UTSL |
|:--|:--|:--|
| 入口基类 | `TerrariaPlugin` + `[ApiVersion(2,1)]` | `BasePlugin` + `[PluginMetadata(...)]` |
| 生命周期 | `Initialize()` / `Dispose(bool)` | `InitializeAsync(...)` / `DisposeAsync(bool)` |
| 包事件 | `GetDataHandlers.Xxx.Register(...)` | `NetPacketHandler.Register<TPacket>(...)` |
| 服务器事件 | `ServerApi.Hooks.*` | `UnifierApi.EventHub.*` / `TShockAPI.Hooks.*` |
| 数据库 | `TShock.DB.Query/QueryReader` | linq2db（`GetTable<T>()` / `CreateTable<T>()`） |
| 日志 | `TShock.Log.ConsoleInfo/ConsoleError` | `TShock.Log.Info/Error`（`Warn` → `Warning`） |
| 配置目录 | `tshock/` | `config/<插件名>/`（`IPluginConfigRegistrar.Directory`） |

## 构建

本仓库的插件工程通过 `template.targets` 引用 UnifierTSL 源码，默认认为它就在**同级目录** `../UnifierTSL`：

```bash
git clone https://github.com/CedaryCat/UnifierTSL.git
dotnet build Plugin.slnx -c Release
```

UTSL 源码在别处时：

```bash
dotnet build Plugin.slnx -c Release -p:UnifierTSLRepo=D:\path\to\UnifierTSL
```

产物输出到 `out/Release/`。
