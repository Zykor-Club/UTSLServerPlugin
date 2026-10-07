# UTSLServerPlugin

这是一个由 ZykorClub 成员维护的 **UnifierTSL (UTSL)** 服务器插件收集仓库。

原 TShock 插件仓库：https://github.com/Zykor-Club/TShockServerPlugin

> ⚠️ UTSL 和 TShock 的插件模型**完全不同**，传统 TShock 插件不能直接放进 UTSL 使用。
> 本仓库里的插件都是**已经改写适配 UTSL** 的版本。

## 插件列表

| 插件 | 原 TShock 版本 | 说明 |
|:--|:--|:--|
| [ItemPool](src/ItemPool) | TShockServerPlugin/src/ItemPool | 物品自选池 |

## 结构

```
UTSLServerPlugin/
├─ UTSLServerPlugin.slnx
└─ src/
   └─ ItemPool/            # 每个插件一个目录
      ├─ ItemPool.csproj
      ├─ Plugin.cs         # UTSL 插件入口（BasePlugin + [PluginMetadata]）
      ├─ Config.cs
      ├─ Database.cs
      ├─ ItemPoolCommands.cs
      ├─ manifest.json
      └─ README.md
```

## 构建

先准备 UTSL 源码（本仓库默认认为它就在同级目录 `../UnifierTSL`）：

```bash
git clone https://github.com/CedaryCat/UnifierTSL.git
cd UnifierTSL && git submodule update --init   # 非必需，但建议
```

然后：

```bash
dotnet build UTSLServerPlugin.slnx -c Release
```

如果 UTSL 源码不在默认位置，用属性覆盖：

```bash
dotnet build UTSLServerPlugin.slnx -c Release -p:UnifierTSLRepo=D:\path\to\UnifierTSL
```

## 部署

把编译出的插件 DLL 放进 UTSL 的 `plugins/` 目录即可（**只放插件自己的 DLL**，不要复制 UnifierTSL.dll / TShockAPI.dll，宿主已经有了）：

```powershell
copy src\ItemPool\bin\Release\net9.0\ItemPool.dll <UTSL目录>\plugins\
```

重启 UTSL，日志里出现 `插件"ItemPool"v...已初始化` 就成功了。

## 从 TShock 插件移植到 UTSL 的主要改动

| 项 | TShock | UTSL |
|:--|:--|:--|
| 入口基类 | `TerrariaPlugin` + `[ApiVersion(2,1)]` | `BasePlugin` + `[PluginMetadata(...)]` |
| 生命周期 | `Initialize()` / `Dispose(bool)` | `InitializeAsync(...)` / `DisposeAsync(bool)` |
| 包事件 | `GetDataHandlers.Xxx.Register(...)` | `NetPacketHandler.Register<TPacket>(...)` |
| 服务器事件 | `ServerApi.Hooks.*` | `UnifierApi.EventHub.*` / `TShockAPI.Hooks.*` |
| 数据库 | `TShock.DB.Query/QueryReader` | linq2db（`TShock.DB.GetTable<T>()` / `CreateTable<T>()`） |
| 日志 | `TShock.Log.ConsoleInfo/ConsoleError` | `TShock.Log.Info/Error`（`Warn` → `Warning`） |
| 配置目录 | `tshock/` | `config/<插件名>/`（`IPluginConfigRegistrar.Directory`） |

详见 UTSL 官方插件开发文档 `docs/dev-plugin.md`。
