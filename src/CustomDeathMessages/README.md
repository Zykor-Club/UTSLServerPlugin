# CustomDeathMessages 自定义死亡消息
- **作者**: Eustia、星梦
- **版本**: v1.0.1

## 功能概述
- 把原版的死亡播报替换成自定义文案
- 支持全部死亡归因：PVP、NPC/Boss、弹幕，以及 21 种环境死亡（摔死 / 溺水 / 岩浆 / 石化 / 刺穿 / 窒息 / 烧死 / 中毒 / 触电 / 逃离血肉墙 / 被舔 / 传送 / 炼狱之火 / 黑暗吞噬 / 饥饿 / 太空 / 挡刀 / 深渊 / 吸血鬼自燃 等）
- 每个死亡类型可配置多条文案，播报时随机取一条
- 支持死亡次数里程碑公告（第 10/50/100/500/1000 次死亡时全服公告）
- 死亡归因依赖反射读取 PlayerDeathReason 的字段，可用 /cdm debug 随时复查

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| /cdm reload | customdeathmessages.reload | 重载配置文件 |
| /cdm debug | customdeathmessages.reload | 调试死亡归因，打印 PlayerDeathReason 反射字段状态 |
| /cdm reset <玩家名> | customdeathmessages.reload | 重置指定玩家的死亡计数 |
| /cdm reset all | customdeathmessages.reload | 重置所有玩家的死亡计数 |

## 配置
配置文件路径：`config/CustomDeathMessages/CustomDeathMessages.json`

```json
{
  "死亡消息模板": {
    "摔死": {
      "颜色（RGB值": "",
      "消息": [
        "[i:321]{Player} 摔成了一滩。",
        "[i:321]{Player} 从高处自由落体。"
      ]
    },
    "NPC击杀": {
      "颜色（RGB值": "205,133,63",
      "消息": ["[i:320]{Player} 被 {NPC} 杀死了。"]
    },
    "烧死": {
      "颜色（RGB值": "255,100,50",
      "消息": ["[i:321]{Player} {Buff}被烧死了。"]
    }
  },
  "死亡次数公告": {
    "10": "公告：{Player} 已经死亡 10 次了！",
    "50": "公告：{Player} 已经死亡 50 次了。"
  }
}
```

死亡消息模板的键共 25 类：摔死 / 溺水 / 岩浆 / 普通 / 击杀 / 石化 / 刺穿 / 窒息 / 烧死 / 中毒 / 触电 / 逃离血肉墙 / 被舔 / 传送 / 炼狱之火 / 黑暗吞噬 / 饥饿 / 太空 / 挡刀 / 深渊 / 吸血鬼自燃 / PVP击杀 / PVP弹幕击杀 / NPC击杀 / 弹幕击杀 / 自定义 / 未知。

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 死亡消息模板 | 对象 | 全部死亡类型各 1~3 条文案 | 各死亡类型的文案表，键为死亡类型名 |
| 颜色（RGB值 | 字符串 | 空（使用默认色） | 该类型消息颜色，格式 "R,G,B"；键名原版即漏了一个右括号，为兼容老配置保持原样 |
| 消息 | 数组 | 每个类型 1~3 条 | 该类型的候选文案，随机取一条 |
| 死亡次数公告 | 对象 | 10 / 50 / 100 / 500 / 1000 | 里程碑公告表，键为死亡次数、值为公告文案 |

## 占位符
| 占位符 | 适用死亡类型 | 说明 |
|--------|:-----------:|------|
| {Player} | 所有 | 死者名字 |
| {Killer} | PVP击杀、PVP弹幕击杀 | 击杀者名字（NPC 击杀时等于 {NPC}） |
| {NPC} | NPC击杀 | 怪物 / BOSS 名称 |
| {Projectile} | PVP弹幕击杀、弹幕击杀 | 弹幕名称 |
| {Item} | PVP击杀、PVP弹幕击杀 | 击杀者手持武器名称 |
| {CustomReason} | 自定义 | 其他插件通过 ByCustomReason 设置的理由（优先级最高） |
| {Buff} | 环境死亡（烧死 / 中毒 / 触电等） | 死亡时检测到的具体 Debuff 名称 |
| {Count} | 死亡次数公告 | 当前总死亡次数 |

## UTSL 移植说明
| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | TerrariaPlugin + [ApiVersion(2,1)] | BasePlugin + [PluginMetadata] |
| 死亡事件 | GetDataHandlers.KillMe（UTSL 已删除） | NetPacketHandler.Register<PlayerDeathV2>；e.PlayerId -> args.Packet.PlayerSlot，e.PlayerDeathReason -> args.Packet.Reason |
| 播报拦截 | ServerApi.Hooks.ServerBroadcast | HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage |
| 每帧清理 | ServerApi.Hooks.GameUpdate | UnifierApi.EventHub.Game.PostUpdate（按 ServerContext 清理） |
| 数据库 | Microsoft.Data.Sqlite + SQLitePCLRaw（需 Batteries.Init()） | 改用 linq2db，走 TShock.DB 的同一个 sqlite 文件；表结构不变，老数据兼容 |
| 多世界 | 全局静态 | server.Main.player[] / server.Main.npc[]；待替换消息按 (ServerContext, 槽位) 分表 |
| 反射 API | TryGetCausingEntity(out Entity) / ByPlayer(int) | TryGetCausingEntity(RootContext, out Entity) / ByPlayer(RootContext, int) |
| 配置目录 | tshock/ | config/CustomDeathMessages/ |
| 日志 | TShock.Log.ConsoleInfo/ConsoleError | Info / Error |

> ⚠️ 每帧清理与死亡检测都依赖世界在 tick —— 没有玩家在线时 UTSL 不 tick，所以本插件的行为要在有玩家时验证。

## 说明
- 每种死亡类型可配多条文案，播报时随机取一条；文案支持占位符。

## 插件版本
### v1.0.1
- 新增死亡次数里程碑公告
- 细化 {Buff} 占位符：按死亡原因检测具体 Debuff

### v1.0.0
- 初始版本

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
