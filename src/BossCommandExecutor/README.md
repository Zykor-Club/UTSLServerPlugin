# BossCommandExecutor Boss击杀命令执行器
- **作者**: 星梦XM
- **版本**: v1.5.0

## 功能概述
- 玩家击败指定 Boss 后，自动执行预设的命令序列
- 通过进度名称 ID（0-22）引用 Boss，简化配置流程
- 支持首次击杀命令与常规执行命令两套命令列表
- 支持执行次数统计与浮动文本显示
- 集成 Boss 伤害排行，可自动全服广播
- 配置自动补全：读取时自动同步进度 ID 与 Boss ID 列表

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| /reload | tshock.cfg.reload | 重载配置文件（订阅 GeneralHooks.ReloadEvent） |

> 本插件未注册独立指令（源码中没有 `/bosscmd`），配置一律通过 TShock 的 `/reload` 重载。

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
  "广播颜色": { "R": 50, "G": 205, "B": 50 },
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
    }
  ]
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 开发者信息 | 数组 | 群号与反馈提示两行 | 仅作说明，无功能作用 |
| 进度名称 | 数组 | 进度 ID 对照表三行 | 仅作说明，无功能作用 |
| 插件开关 | 布尔值 | true | 插件总开关 |
| 命令执行延迟(毫秒) | 整数 | 100 | 执行命令序列前的延迟 |
| 广播执行结果 | 布尔值 | true | 是否全服广播执行结果 |
| 广播消息格式 | 字符串 | [c/32CD32:Boss命令] {boss} 已被击败，已自动执行 {count} 条命令。 | 广播文案 |
| 广播颜色 | 对象 | R=50, G=205, B=50 | 广播颜色 |
| 控制台提示 | 布尔值 | true | 是否在控制台打印每条成功执行的命令 |
| 自动广播BOSS伤害排行 | 布尔值 | true | 是否自动广播 Boss 伤害排行 |
| Boss命令配置 | 数组 | 22 个原版 Boss 各一条 | Boss 命令配置列表 |
| Boss名称 | 字符串 | 空 | Boss 名称，仅用于显示与广播 |
| Boss ID列表 | 数组 | [] | 该 Boss 对应的 NPC ID；为空时按进度名称ID 自动补全 |
| 进度名称ID | 整数 | -1 | 进度名称 ID（0-22）；为 -1 时按 Boss ID列表 反推 |
| 需要被召唤 | 布尔值 | true | 是否只处理被召唤出来的该 Boss |
| 记录执行次数 | 布尔值 | true | 是否累计执行次数 |
| 执行次数 | 整数 | 0 | 已累计执行次数（运行时写回） |
| 已首次击杀 | 布尔值 | false | 是否已执行过首次击杀命令（运行时写回） |
| 首次击杀命令 | 数组 | [] | 首次击杀时执行的命令 |
| 常规执行命令 | 数组 | [] | 每次击杀都会执行的命令 |
| 广播结果 | 布尔值 | true | 该 Boss 是否广播执行结果 |
| 浮动文本内容 | 字符串 | Use /bag rall to receive the Progressbag | 浮动文本内容 |
| 浮动文本颜色 | 对象 | R=102, G=204, B=255 | 浮动文本颜色 |
| 浮动文本显示时长(秒) | 整数 | 10 | 浮动文本显示时长 |
| 启用浮动文本 | 布尔值 | true | 是否显示浮动文本 |

## 占位符
| 占位符 | 说明 | 示例 |
|--------|:----:|------|
| {boss} | Boss 名称 | "血肉墙" |
| {boss.name} | Boss 名称（同 {boss}） | "血肉墙" |
| {boss.id} | Boss 的 NPC ID | "113" |
| {boss.type} | Boss 类型 | "113" |
| {time} | 当前时间 | "14:30:25" |
| {date} | 当前日期 | "2024-01-15" |
| {count} | 已累计执行次数 | "5" |
| {exec_count} | 已累计执行次数（同 {count}） | "10" |
| {world} | 世界名称 | "新世界" |
| {players} | 在线玩家数量 | "5" |

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

## UTSL 移植说明
| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | TerrariaPlugin + [ApiVersion(2,1)] | BasePlugin + [PluginMetadata] |
| Boss 生成 | ServerApi.Hooks.NpcSpawn | HookEvents.Terraria.NPC.OnSpawn |
| Boss 死亡 | ServerApi.Hooks.NpcKilled | HookEvents.Terraria.NPC.checkDead |
| 伤害追踪钩子 | On.Terraria.GameContent.BossDamageTracker.OnBossKilled | 签名未变，可照搬；用 FindServerForNpc 反查所属世界 |
| 命令执行 | Commands.HandleCommand(TSPlayer.Server, cmd) | 先走 V2 声明式派发，未匹配再回退 Commands.HandleCommand(new CommandExecutor(server, byte.MaxValue), cmd) |
| 发包枚举 | Terraria 的 PacketTypes | TrProtocol.MessageID |
| 浮动文本 | NetMessage.SendData | server.NetMessage.SendData |
| Main.npc / Main.worldName | 全局静态 | server.Main.npc / server.Main.worldName |
| 玩家筛选 | TShock.Players | TShock.Players + 按 GetCurrentServer() 过滤（多世界） |
| 配置 | Newtonsoft.Json，tshock/ | System.Text.Json，config/BossCommandExecutor/ |
| 日志 | TShock.Log.ConsoleInfo/Error/Debug | Info / Error / Debug |

> ⚠️ UTSL 关键差异：绝大多数 TShock 命令（bc / say / give / time / spawnmob ...）已迁到 V2 声明式命令体系，不在 Commands.ChatCommands 里。直接调 Commands.HandleCommand 会静默什么都不做（返回 true 但不执行），所以本移植先走 V2 派发。
>
> ⚠️ 老式钩子 `OTAPI.Hooks.NPC.*`（Spawn / Killed / DropLoot / BossBag）在 UTSL 的 OTAPI 分支里被移除了，必须改用 `HookEvents.Terraria.NPC.*`。
>
> ⚠️ 多世界注意：BossTracker 的所有索引都带 ServerContext —— UTSL 是单进程多世界，NPC 槽位在每个世界都会重复。
>
> 测试提示：NpcSpawn / NpcKilled 的触发需要世界在 tick（有玩家在线），空服下用 server.NPC.NewNPC() 生成的 NPC 走不到完整路径。

## 说明
- 命令以服务器控制台身份执行；首次击杀与常规击杀可用两套命令列表；进度 ID 0-22 对应游戏进度。

## 插件版本
### v1.5.0
- 重构一堆石，变成了新的石（乐）
- 修复了多体节 boss 的击杀判定问题

### v1.3.0
- 整合 BOSS 伤害排行功能
- 添加 `自动广播BOSS伤害排行` 配置项
- 优化伤害排行显示，包含队伍颜色、DPS、MVP 标记

### v1.2.0
- 添加执行次数统计功能
- 支持浮动文本显示
- 添加首次击杀机制
- 支持所有泰拉瑞亚原版 Boss

### v1.1.0
- 初始版本发布
- 基础 Boss 命令执行功能
- 支持占位符替换

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
