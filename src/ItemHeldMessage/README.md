# ItemHeldMessage 手持物品提示

- **作者**: 淦 & 星梦XM（优化版）
- **版本**: 1.1.4
- **出处**: `https://github.com/Zykor-Club/UTSLServerPlugin`（原 TShock 版：`Zykor-Club/TShockServerPlugin`）

## 插件简介

当玩家手持特定物品时，在头顶显示彩色浮动提示，或在信息栏显示文本，并可自动执行预设命令。

## 核心功能

### 1. 浮动文本 / 信息栏提示
- 两种展示方式可选，每个物品可配多条消息随机显示
- 颜色可自定义，浮动文本支持 Y 轴偏移
- 玩家可用 `/ihm mode` 自行切换：0 关闭 / 1 浮动 / 2 信息栏 / 3 全部

### 2. 自动命令执行
- 手持指定物品自动执行命令，支持按**用户组**限制
- 变量替换：`{player}` 玩家名、`{item}` 物品ID、`{x}` / `{y}` 坐标

### 3. 冷却机制
- **手持切换冷却**：防止滚轮快速切换刷屏（默认 1.5 秒）
- 浮动文本 / 信息栏 / 命令各自独立冷却，且物品级可覆盖全局

## 指令

| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/ihm` | `itemheldmsg.use` | 显示帮助 |
| `/ihm mode [0-3]` | `itemheldmsg.use` | 设置显示模式 |
| `/ihm status` | `itemheldmsg.use` | 查看当前状态 |
| `/ihm check <物品ID或名称>` | `itemheldmsg.use` | 查看某物品的配置详情 |
| `/ihm reload` | `itemheldmsg.admin` | 重载配置 |
| `/reload` | `tshock.cfg.reload` | 也可重载本插件配置 |

## 配置

> UTSL 配置文件路径：`config/ItemHeldMessage/ItemHeldMessages.json`
> （原 TShock 版为 `tshock/ItemHeldMessages.json`，JSON 结构完全一致）

**全局设置**

| 配置项 | 类型 | 默认 | 说明 |
|:--|:--|:--:|:--|
| 启用浮动文本 | bool | true | 总开关 |
| 启用信息栏文本 | bool | true | 总开关 |
| 启用自动命令 | bool | true | 总开关 |
| 手持切换冷却(秒) | double | 1.5 | 切换物品后的强制等待 |
| 默认浮动文本冷却(秒) | double | 3.0 | 可被物品覆盖 |
| 默认信息栏冷却(秒) | double | 2.0 | 可被物品覆盖 |
| 默认命令冷却(秒) | double | 5.0 | 可被物品覆盖 |
| 默认Y轴偏移(像素) | float | 50 | 浮动文本高度 |
| 全局跳过命令权限检测 | bool | false | 打开后所有物品的命令都不查权限组 |

**物品配置**（键为物品 ID 字符串，如 `"29"`）

```json5
"29": {
  "物品名称": "生命水晶",
  "浮动消息列表": [ { "文本": "...", "颜色": [0, 100, 255] } ],
  "信息栏消息列表": [ { "文本": "...", "颜色": [0, 255, 150] } ],
  "命令配置": {
    "启用": true,
    "命令": "/heal 20",
    "允许的权限组": ["admin", "vip"],
    "描述": "恢复20点生命值",
    "跳过权限检测": false
  },
  "自定义覆盖设置": { "Y轴偏移": 60, "浮动文本冷却": 5, "信息栏冷却": 3, "命令冷却": 0 }
}
```

## UTSL 移植说明

| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | `TerrariaPlugin` + `[ApiVersion(2,1)]` | `BasePlugin` + `[PluginMetadata]` |
| 每帧检测 | `ServerApi.Hooks.GameUpdate` | `UnifierApi.EventHub.Game.PostUpdate`（按 `ServerContext` 遍历） |
| 玩家离开 | `ServerApi.Hooks.ServerLeave` | `UnifierApi.EventHub.Netplay.LeaveEvent` |
| 进入提示 | `ServerApi.Hooks.NetGreetPlayer` | `TShockAPI.Hooks.PlayerHooks.PlayerPostLogin`（UTSL 无 NetGreetPlayer；未登录的 guest 不会收到欢迎语） |
| 浮动文本 | `NetMessage.SendData` | `server.NetMessage.SendData`（USP 把 NetMessage 上下文化了） |
| 发包枚举 | Terraria 的 `PacketTypes` | `global using PacketTypes = TrProtocol.MessageID;`（插件里需自己声明这行别名） |
| 执行命令 | `Commands.HandleCommand(player, cmd)` | `Commands.HandleCommand(new CommandExecutor(server, (byte)player.Index), cmd)` |
| 配置目录 | `tshock/` | `config/ItemHeldMessage/` |
| 日志 | `TShock.Log.ConsoleInfo/Warn` | `Info` / `Warning` |
| 会话表 | 按 `player.Index` | 按 **`(ServerContext, 槽位)`**，避免多世界槽位冲突 |

> ⚠️ **本插件的每帧检测需要玩家在线才会运行** —— UTSL 在没有玩家在线时世界不 tick
> （`Game.PostUpdate` 等每帧事件都不会触发）。这正好是它该生效的场合，但空服无法验证。
>
> ⚠️ 上游的判定要求 `player.IsLoggedIn`，**未登录的 guest 玩家不会触发手持提示** —— 这是原插件的
> 固有行为，移植时保持了一致，没有擅自放宽。

## 更新日志

### v1.1.4
- 自动命令支持跳过权限检测（全局 + 单物品）
- 修复若干冷却与显示问题
