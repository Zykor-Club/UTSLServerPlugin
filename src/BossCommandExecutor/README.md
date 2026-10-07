# BossCommandExecutor Boss击杀命令执行器

- **作者**: 星梦
- **版本**: v1.5.0

## 功能概述

- 当玩家击败指定的Boss后，自动执行预设的命令序列
- 通过进度名称ID（0-22）引用Boss，简化配置流程
- 配置自动补全：reload时自动同步进度ID和Boss ID列表

## 指令

| 语法 | 权限 | 说明 |
|------|:----:|:-----:|
| `/bosscmd reload` | `bosscommandexecutor.reload` | 重载配置文件 |

## 占位符

| 占位符 | 说明 | 示例 |
|--------|:----:|------|
| `{boss}` | Boss名称 | "血肉墙" |
| `{boss.name}` | Boss名称（同{boss}） | "血肉墙" |
| `{boss.id}` | Boss ID | "113" |
| `{boss.type}` | Boss类型 | "113" |
| `{time}` | 当前时间 | "14:30:25" |
| `{date}` | 当前日期 | "2024-01-15" |
| `{count}` | 执行的命令数量 | "5" |
| `{exec_count}` | 累计执行次数 | "10" |
| `{world}` | 世界名称 | "新世界" |
| `{players}` | 在线玩家数量 | "5" |

## 进度名称ID列表

| 进度名称 | 进度ID | Boss名称 | NPC ID |
|----------|:------:|----------|--------|
| 无 | 0 | - | - |
| 史莱姆王 | 1 | 史莱姆王 | 50 |
| 克眼 | 2 | 克苏鲁之眼 | 4 |
| 世吞 | 3 | 世界吞噬者 | 13,14,15 |
| 克脑 | 4 | 克苏鲁之脑 | 266 |
| 蜂王 | 5 | 蜂后 | 222 |
| 骷髅王 | 6 | 骷髅王 | 35 |
| 鹿角怪 | 7 | 巨鹿 | 668 |
| 困难模式(肉山) | 8 | 血肉墙 | 113,114 |
| 史莱姆皇后 | 9 | 史莱姆之皇 | 657 |
| 毁灭者 | 10 | 毁灭者 | 134,135,136 |
| 双子魔眼 | 11 | 双子魔眼 | 125,126 |
| 机械骷髅王 | 12 | 机械骷髅王 | 127 |
| 世纪之花 | 13 | 世纪之花 | 262 |
| 石巨人 | 14 | 石巨人 | 245 |
| 猪鲨 | 15 | 猪龙鱼公爵 | 370 |
| 光女 | 16 | 光之女皇 | 636 |
| 教徒 | 17 | 拜月教徒 | 440 |
| 日耀柱 | 18 | 日耀柱 | 390 |
| 星云柱 | 19 | 星云柱 | 391 |
| 星璇柱 | 20 | 星璇柱 | 392 |
| 星尘柱 | 21 | 星尘柱 | 393 |
| 月总 | 22 | 月亮领主 | 398 |

## 配置

配置文件路径：`config/BossCommandExecutor/BossCommands.json`

```json
{
  "开发者信息": [
    "[开发者所在群]QQ群：Tshock:816771079",
    "[问题反馈] 问题询问前，请将报错截图，配置文件一同向群内发送"
  ],
  "进度名称": [
    "无 0 | 史莱姆王 1 | 克眼 2 | 世吞 3 | 克脑 4 | 蜂王 5 | 骷髅王 6 | 鹿角怪 7 | 困难模式(肉山) 8 | 史莱姆皇后 9 |",
    "| 毁灭者 10 | 双子魔眼 11 | 机械骷髅王 12 | 世纪之花 13 | 石巨人 14 | 猪鲨 15 | 光女 16 |",
    "教徒 17 | 日耀柱 18 | 星云柱 19 | 星璇柱 20 | 星尘柱 21 | 月总 22 |"
  ],
  "插件开关": true,
  "命令执行延迟(毫秒)": 100,
  "广播执行结果": true,
  "广播消息格式": "[c/32CD32:Boss命令] {boss} 已被击败，已自动执行 {count} 条命令。",
  "广播颜色": {
    "R": 50,
    "G": 205,
    "B": 50
  },
  "控制台提示": true,
  "自动广播BOSS伤害排行": true,
  "Boss命令配置": [
    {
      "Boss名称": "史莱姆王",
      "进度名称ID": 1,
      "Boss ID列表": [50],
      "需要被召唤": false,
      "记录执行次数": true,
      "执行次数": 0,
      "已首次击杀": false,
      "首次击杀命令": ["/bc 恭喜玩家首次击杀史莱姆王！", "/give 74 *all* 1"],
      "常规执行命令": [],
      "广播结果": true,
      "浮动文本内容": "Use /bag rall to receive the Progressbag",
      "浮动文本颜色": { "R": 102, "G": 204, "B": 255 },
      "浮动文本显示时长(秒)": 10,
      "启用浮动文本": true
    },
    {
      "Boss名称": "毁灭者",
      "进度名称ID": 10,
      "Boss ID列表": [134, 135, 136],
      "需要被召唤": true,
      "记录执行次数": true,
      "执行次数": 0,
      "已首次击杀": false,
      "首次击杀命令": ["/bc 恭喜玩家首次击杀毁灭者！", "/give 74 *all* 1"],
      "常规执行命令": [],
      "广播结果": true,
      "浮动文本内容": "Use /bag rall to receive the Progressbag",
      "浮动文本颜色": { "R": 102, "G": 204, "B": 255 },
      "浮动文本显示时长(秒)": 10,
      "启用浮动文本": true
    },
    {
      "Boss名称": "月亮领主",
      "进度名称ID": 22,
      "Boss ID列表": [398],
      "需要被召唤": true,
      "记录执行次数": true,
      "执行次数": 0,
      "已首次击杀": false,
      "首次击杀命令": ["/bc 恭喜玩家首次击杀月亮领主！", "/give 74 *all* 1", "giveall 74 10"],
      "常规执行命令": ["time 0", "bc 服务器将在1分钟后重启", "settimer 60 restart"],
      "广播结果": true,
      "浮动文本内容": "Use /bag rall to receive the Progressbag",
      "浮动文本颜色": { "R": 102, "G": 204, "B": 255 },
      "浮动文本显示时长(秒)": 10,
      "启用浮动文本": true
    }
  ]
}
```

## 插件版本

### v1.5.0
- 重构一大堆石，变成了新的石（乐）
- 修复了多体节boss的击杀判定问题

### v1.3.0
- 整合BOSS伤害排行功能
- 添加`自动广播BOSS伤害排行`配置项
- 优化伤害排行显示，包含队伍颜色、DPS、MVP标记

### v1.2.0
- 添加执行次数统计功能
- 支持浮动文本显示
- 添加首次击杀机制
- 支持所有泰拉瑞亚原版Boss

### v1.1.0
- 初始版本发布
- 基础Boss命令执行功能
- 支持占位符替换

## 反馈
- 优先发issued -> 星梦的插件库：https://github.com/Zykor-Club
- 次优先：TShock官方群：816771079


## UTSL 移植说明

| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | `TerrariaPlugin` + `[ApiVersion(2,1)]` | `BasePlugin` + `[PluginMetadata]` |
| **Boss 生成** | `ServerApi.Hooks.NpcSpawn`（底层是 `OTAPI.Hooks.NPC.Spawn`） | **UTSL 里 `OTAPI.Hooks.NPC.Spawn` 不存在**（编译期 CS0117: Hooks.NPC 未包含 Spawn 的定义），改用 `HookEvents.Terraria.NPC.OnSpawn` |
| **Boss 死亡** | `ServerApi.Hooks.NpcKilled`（底层是 `OTAPI.Hooks.NPC.Killed`） | 同理，改用 `HookEvents.Terraria.NPC.checkDead` |
| 伤害追踪钩子 | `On.Terraria.GameContent.BossDamageTracker.OnBossKilled` | **签名未变**（没被上下文化），可照搬；但它不给 ServerContext，用 `FindServerForNpc` 按实例引用反查（UTSL 的 NPC 上没有 root 成员，反射已确认） |
| 命令执行 | `Commands.HandleCommand(TSPlayer.Server, cmd)` | `Commands.HandleCommand(new CommandExecutor(server, byte.MaxValue), cmd)` |
| 发包枚举 | Terraria 的 `PacketTypes` | `TrProtocol.MessageID`（浮动文本用 `CreateCombatTextExtended`） |
| 浮动文本 | `NetMessage.SendData` | `server.NetMessage.SendData`（USP 把 NetMessage 上下文化了） |
| `Main.npc` / `Main.worldName` | 全局静态 | `server.Main.npc` / `server.Main.worldName` |
| 玩家筛选 | `TShock.Players` | `TShock.Players` + 按 `GetCurrentServer()` 过滤（多世界） |
| 配置 | Newtonsoft.Json，`tshock/` | System.Text.Json，`config/BossCommandExecutor/` |
| 日志 | `TShock.Log.ConsoleInfo/Error/Debug` | `Info` / `Error` / `Debug` |

> **一个重要发现**：TSAPI 依赖的那套老式手写钩子 `OTAPI.Hooks.NPC.*`（`Spawn` / `Killed` / `DropLoot` / `BossBag`）
> 在 UTSL 的 OTAPI 分支里**被移除了**。任何直接依赖 `ServerApi.Hooks.NpcXxx` 的原版插件都得改用
> `HookEvents.Terraria.NPC.*`。好消息是 HookEvents 版本**额外带了 `RootContext`**，多世界下反而更准确。
>
> **多世界注意**：`BossTracker` 的所有索引都补上了 `ServerContext` 维度 —— UTSL 是单进程多世界，
> NPC 槽位在每个世界都会重复，不区分世界会让一个世界的 Boss 顶掉另一个世界的记录。
>
> **测试提示**：`NpcSpawn` / `NpcKilled` 的真实触发需要世界在 tick（即有玩家在线）。
> 空服下用 `server.NPC.NewNPC()` 直接生成的 NPC 走不到完整路径（伤害追踪钩子不会触发），
> 所以这个插件必须在**有玩家进服**的情况下验证。
