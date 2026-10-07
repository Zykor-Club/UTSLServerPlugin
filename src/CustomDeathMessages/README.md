# CustomDeathMessages 自定义死亡消息

- **作者**: Eustia、星梦
- **出处**: `https://github.com/Zykor-Club/UTSLServerPlugin`（原 TShock 版：`Zykor-Club/TShockServerPlugin`）

## 功能概述

把原版的死亡播报替换成自定义文案，支持**全部死亡归因**：PVP、NPC/Boss、弹幕、以及 21 种环境死亡
（摔死 / 溺水 / 岩浆 / 石化 / 刺穿 / 窒息 / 烧死 / 中毒 / 触电 / 逃离血肉墙 / 被舔 / 传送 / 炼狱之火 /
黑暗吞噬 / 饥饿 / 太空 / 挡刀 / 深渊 / 吸血鬼自燃 …）。

另外支持**死亡次数里程碑公告**（第 10/50/100/500/1000 次死亡时全服公告）。

## 指令

| 语法 | 权限 | 说明 |
|------|:----:|:-----|
| `/cdm reload` | `customdeathmessages.reload` | 重载配置文件 |
| `/cdm debug` | `customdeathmessages.reload` | 调试死亡归因，打印 `PlayerDeathReason` 反射字段状态 |
| `/cdm reset <玩家名>` | `customdeathmessages.reload` | 重置指定玩家的死亡计数 |
| `/cdm reset all` | `customdeathmessages.reload` | 重置所有玩家的死亡计数 |

## 占位符

| 占位符 | 适用死亡类型 | 说明 |
|--------|:-----------:|:-----|
| `{Player}` | 所有 | 死者名字 |
| `{Killer}` | PVP击杀、PVP弹幕击杀 | 击杀者名字（NPC 击杀时等于 `{NPC}`） |
| `{NPC}` | NPC击杀 | 怪物/BOSS 名称 |
| `{Projectile}` | PVP弹幕击杀、弹幕击杀 | 弹幕名称 |
| `{Item}` | PVP击杀、PVP弹幕击杀 | 击杀者手持武器名称 |
| `{CustomReason}` | 自定义 | 其他插件通过 `ByCustomReason` 设置的理由（优先级最高） |
| `{Buff}` | 环境死亡（烧死/中毒/触电等） | 死亡时检测到的具体 Debuff 名称 |
| `{Count}` | 死亡次数公告 | 当前总死亡次数 |

## 配置

> UTSL 配置文件路径：`config/CustomDeathMessages/CustomDeathMessages.json`
> （原 TShock 版为 `tshock/CustomDeathMessages.json`，JSON 结构完全一致，老配置可直接搬过来）

```json5
{
  "死亡消息模板": {
    "摔死": {
      "颜色（RGB值": "",            // 如 "225,100,100"，留空用默认色 (225,100,100)
      "消息": [
        "[i:321]{Player} 摔成了一滩。",
        "[i:321]{Player} 从高处自由落体。"
      ]
    },
    "NPC击杀": {
      "颜色（RGB值": "205,133,63",  // 棕色
      "消息": [ "[i:320]{Player} 被 {NPC} 杀死了。" ]
    },
    "烧死": {
      "颜色（RGB值": "255,100,50",
      "消息": [ "[i:321]{Player} {Buff}被烧死了。" ]
    }
    // ... 其余分类：溺水/岩浆/普通/击杀/石化/刺穿/窒息/中毒/触电/逃离血肉墙/
    //     被舔/传送/炼狱之火/黑暗吞噬/饥饿/太空/挡刀/深渊/吸血鬼自燃/
    //     PVP击杀/PVP弹幕击杀/弹幕击杀/自定义/未知
  },
  "死亡次数公告": {
    "10": "公告：{Player} 已经死亡 10 次了！",
    "50": "公告：{Player} 已经死亡 50 次了。"
  }
}
```

> 注意：`颜色（RGB值` 这个键**原版就漏了一个右括号**，为保证老配置文件兼容，移植时保持原样，没有"修正"。

## UTSL 移植说明

| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | `TerrariaPlugin` + `[ApiVersion(2,1)]` | `BasePlugin` + `[PluginMetadata]` |
| **死亡事件** | `GetDataHandlers.KillMe`（**UTSL 已删除该事件**） | `NetPacketHandler.Register<PlayerDeathV2>`；字段一一对应：`e.PlayerId` → `args.Packet.PlayerSlot`，`e.PlayerDeathReason` → `args.Packet.Reason` |
| **播报拦截** | `ServerApi.Hooks.ServerBroadcast`（UTSL 无对应事件） | `HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage`（OTAPI 的 HookEvents），字段 `args.text` / `args.color` 对应上游的 `e.Message` / `e.Color` |
| 每帧清理 | `ServerApi.Hooks.GameUpdate` | `UnifierApi.EventHub.Game.PostUpdate`（按 `ServerContext` 清理） |
| **数据库** | 自带 `Microsoft.Data.Sqlite` + `SQLitePCLRaw`（需 `Batteries.Init()`） | **改用 linq2db**，走 `TShock.DB` 的同一个 sqlite 文件；表结构不变，老数据兼容 |
| 多世界 | 全局静态 | `server.Main.player[]` / `server.Main.npc[]`；待替换消息按 `(ServerContext, 槽位)` 分表 |
| 反射 API | `TryGetCausingEntity(out Entity)` / `ByPlayer(int)` | **USP 上下文化**：`TryGetCausingEntity(RootContext, out Entity)` / `ByPlayer(RootContext, int)`（`ByNPC` / `ByOther` 未变） |
| 配置目录 | `tshock/` | `config/CustomDeathMessages/` |
| 日志 | `TShock.Log.ConsoleInfo/ConsoleError` | `Info` / `Error` |

> **死亡归因依赖反射**读取 `PlayerDeathReason` 的私有字段（`_sourcePlayerIndex` 等，OTAPI 下已是 public）。
> 实测 8 个字段在 UTSL 下全部存在；`/cdm debug` 可随时复查。
>
> ⚠️ 每帧清理与死亡检测都依赖世界在 tick —— **没有玩家在线时 UTSL 不 tick**，所以本插件的行为要在有玩家时验证。

## 更新日志

### v1.0.1
- 新增死亡次数里程碑公告
- 细化 `{Buff}` 占位符：按死亡原因检测具体 Debuff

### v1.0.0
- 初始版本
