# ItemHeldMessage 手持物品提示
- **作者**: 淦 & 星梦XM
- **版本**: v1.1.4

## 功能概述
- 玩家手持特定物品时，在头顶显示彩色浮动提示，或在信息栏显示文本，并可自动执行预设命令
- 两种展示方式可选，每个物品可配多条消息随机显示；颜色可自定义，浮动文本支持 Y 轴偏移
- 玩家可用 /ihm mode 自行切换：0 关闭 / 1 浮动 / 2 信息栏 / 3 全部
- 自动命令支持按用户组限制，变量替换 {player} / {item} / {x} / {y}
- 冷却机制：手持切换冷却防滚轮刷屏（默认 1.5 秒）；浮动文本 / 信息栏 / 命令各自独立冷却，物品级可覆盖全局

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| /ihm | itemheldmsg.use | 显示帮助 |
| /ihm mode [0-3] | itemheldmsg.use | 设置显示模式（0 关 / 1 浮动 / 2 信息栏 / 3 全部） |
| /ihm status | itemheldmsg.use | 查看当前状态 |
| /ihm check <物品ID或名称> | itemheldmsg.use | 查看某物品的配置详情 |
| /ihm reload | itemheldmsg.admin | 重载配置 |
| /reload | itemheldmsg.admin | 也可重载本插件配置 |

## 配置
配置文件路径：`config/ItemHeldMessage/ItemHeldMessages.json`

```json
{
  "全局设置": {
    "启用浮动文本": true,
    "启用信息栏文本": true,
    "启用自动命令": true,
    "手持切换冷却(秒)": 1.5,
    "默认浮动文本冷却(秒)": 3.0,
    "默认信息栏冷却(秒)": 2.0,
    "默认命令冷却(秒)": 5.0,
    "默认Y轴偏移(像素)": 50,
    "全局跳过命令权限检测": false
  },
  "物品配置": {
    "29": {
      "物品名称": "生命水晶",
      "浮动消息列表": [ { "文本": "关住星梦喵", "颜色": [0, 100, 255] } ],
      "信息栏消息列表": [ { "文本": "你将生命水晶贴近了你的耳朵", "颜色": [0, 255, 150] } ],
      "命令配置": {
        "启用": true,
        "命令": "/heal 20",
        "允许的权限组": ["admin", "vip"],
        "描述": "恢复20点生命值",
        "跳过权限检测": false
      },
      "自定义覆盖设置": { "Y轴偏移": 60, "浮动文本冷却": 5, "信息栏冷却": 3, "命令冷却": 0 }
    }
  }
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 全局设置 | 对象 | 见下 | 全局默认值 |
| 全局设置.启用浮动文本 | 布尔值 | true | 浮动文本总开关 |
| 全局设置.启用信息栏文本 | 布尔值 | true | 信息栏文本总开关 |
| 全局设置.启用自动命令 | 布尔值 | true | 自动命令总开关 |
| 全局设置.手持切换冷却(秒) | 浮点数 | 1.5 | 切换物品后的强制等待 |
| 全局设置.默认浮动文本冷却(秒) | 浮点数 | 3.0 | 可被物品级覆盖 |
| 全局设置.默认信息栏冷却(秒) | 浮点数 | 2.0 | 可被物品级覆盖 |
| 全局设置.默认命令冷却(秒) | 浮点数 | 5.0 | 可被物品级覆盖 |
| 全局设置.默认Y轴偏移(像素) | 浮点数 | 50 | 浮动文本高度 |
| 全局设置.全局跳过命令权限检测 | 布尔值 | false | 打开后所有物品的命令都不查权限组 |
| 物品配置 | 对象 | 示例："29" 与 "74" | 键为物品 ID 字符串，值为该物品的配置 |
| 物品配置.物品名称 | 字符串 | 空 | 显示用名称 |
| 物品配置.浮动消息列表 | 数组 | [] | 浮动文本候选，每项含 文本 / 颜色 |
| 物品配置.信息栏消息列表 | 数组 | [] | 信息栏候选，每项含 文本 / 颜色 |
| 物品配置.命令配置.启用 | 布尔值 | false | 是否启用自动命令 |
| 物品配置.命令配置.命令 | 字符串 | 空 | 要执行的命令 |
| 物品配置.命令配置.允许的权限组 | 数组 | [] | 允许触发该命令的用户组 |
| 物品配置.命令配置.描述 | 字符串 | 空 | 命令说明 |
| 物品配置.命令配置.跳过权限检测 | 布尔值 | false | 该命令是否跳过权限组校验 |
| 物品配置.自定义覆盖设置 | 对象 | 空 | 覆盖全局默认值，含 Y轴偏移 / 浮动文本冷却 / 信息栏冷却 / 命令冷却 |
| 消息.颜色 | 数组 | [255,255,255] | RGB 三元组 |

## UTSL 移植说明
| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | TerrariaPlugin + [ApiVersion(2,1)] | BasePlugin + [PluginMetadata] |
| 每帧检测 | ServerApi.Hooks.GameUpdate | UnifierApi.EventHub.Game.PostUpdate（按 ServerContext 遍历） |
| 玩家离开 | ServerApi.Hooks.ServerLeave | UnifierApi.EventHub.Netplay.LeaveEvent |
| 进入提示 | ServerApi.Hooks.NetGreetPlayer | TShockAPI.Hooks.PlayerHooks.PlayerPostLogin |
| 浮动文本 | NetMessage.SendData | server.NetMessage.SendData |
| 发包枚举 | Terraria 的 PacketTypes | global using PacketTypes = TrProtocol.MessageID |
| 执行命令 | Commands.HandleCommand(player, cmd) | Commands.HandleCommand(new CommandExecutor(server, (byte)player.Index), cmd) |
| 配置目录 | tshock/ | config/ItemHeldMessage/ |
| 日志 | TShock.Log.ConsoleInfo/Warn | Info / Warning |
| 会话表 | 按 player.Index | 按 (ServerContext, 槽位)，避免多世界槽位冲突 |

> ⚠️ 每帧检测需要玩家在线才会运行 —— UTSL 在没有玩家在线时世界不 tick，空服无法验证。
>
> ⚠️ 上游的判定要求 player.IsLoggedIn，未登录的 guest 玩家不会触发手持提示 —— 这是原插件的固有行为，移植时保持了一致。

## 说明
- 浮动文本发给附近玩家，信息栏只给自己；玩家可用 `/ihm mode` 自行切换显示方式。

## 插件版本
### v1.1.4
- 自动命令支持跳过权限检测（全局 + 单物品）
- 修复若干冷却与显示问题

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
