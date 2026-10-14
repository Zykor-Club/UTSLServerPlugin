# BetterBack 更好的死亡返回
- **作者**: 星梦XM
- **版本**: v2026.5.2.0

## 功能概述
- 记录玩家死亡位置，支持快速传送返回
- 支持自定义传送 Buff 和无敌时间
- 支持死亡后自动返回死亡点的倒计时
- 未击败骷髅王前不记录地牢死亡点，未击败世纪之花前不记录神庙死亡点
- 死亡点是内存态，服务器重启后全部丢失（与原插件行为一致）

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| /bet | betterback.use | 传送至最新死亡点 |
| /bet [序号] | betterback.use | 传送至指定死亡点 |
| /bet list | betterback.use | 查看死亡点列表 |
| /bet clear | betterback.use | 清除所有死亡点 |
| /bet auto <秒/off> | betterback.use | 设置死亡后自动返回倒计时 |
| /bet help | betterback.use | 显示帮助信息 |
| /betbuff add <id> | betterback.buff | 添加自定义传送 Buff |
| /betbuff remove <id> | betterback.buff | 移除自定义传送 Buff |
| /betbuff list | betterback.buff | 查看自定义传送 Buff 列表 |
| /betgod time <秒> | betterback.god | 设置无敌时间 |
| /betgod info | betterback.god | 查看当前无敌时间 |
| /reload | tshock.cfg.reload | 重载插件配置 |

## 配置
配置文件路径：`config/BetterBack/BetterBack.json`

```json
{
  "最大死亡点数量": 5,
  "传送冷却时间": 30,
  "默认传送BuffID": [1, 3, 5],
  "Buff持续时间": 10,
  "无敌时间": 5,
  "死亡点记录消息": "已记录死亡点 ({0}/{1})",
  "传送成功消息": "已传送至死亡点: {0}",
  "无敌时间消息": "您获得了 {0} 秒无敌时间",
  "冷却时间消息": "请等待 {0:F1} 秒后再试",
  "禁止记录未击败骷髅王的地牢死亡": true,
  "禁止记录未击败世纪之花的神庙死亡": true
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 最大死亡点数量 | 整数 | 5 | 每个玩家最多记录的死亡点数量 |
| 传送冷却时间 | 整数 | 30 | 传送冷却时间（秒） |
| 默认传送BuffID | 数组 | [1,3,5] | 传送后自动给予的 Buff ID 列表 |
| Buff持续时间 | 整数 | 10 | Buff 持续时间（秒） |
| 无敌时间 | 整数 | 5 | 传送后无敌时间（秒） |
| 死亡点记录消息 | 字符串 | 已记录死亡点 ({0}/{1}) | 记录死亡点时显示的消息 |
| 传送成功消息 | 字符串 | 已传送至死亡点: {0} | 传送成功时显示的消息 |
| 无敌时间消息 | 字符串 | 您获得了 {0} 秒无敌时间 | 获得无敌时间时显示的消息 |
| 冷却时间消息 | 字符串 | 请等待 {0:F1} 秒后再试 | 冷却中时显示的消息 |
| 禁止记录未击败骷髅王的地牢死亡 | 布尔值 | true | 是否禁止记录地牢死亡点 |
| 禁止记录未击败世纪之花的神庙死亡 | 布尔值 | true | 是否禁止记录神庙死亡点 |

## 权限
| 权限节点 | 说明 |
|----------|------|
| betterback.use | 记录死亡点 + /bet 全部子命令 |
| betterback.buff | /betbuff |
| betterback.god | /betgod |
| betterback.admin | 预留（当前代码未使用） |

以上权限是**插件自定义**的，TShock 不会自动授予 —— 没授予的话**死亡点会静默不记录**，表现为"死了没反应、/bet 说没有"。插件启动时会在日志里打印提示：

~~~
[BetterBack] 需要的权限（请授予玩家组）: betterback.use(记录/传送) betterback.buff betterback.god
~~~

授权示例：

~~~
/group addperm default betterback.use
/group addperm admin betterback.buff
/group addperm admin betterback.god
~~~

## UTSL 移植说明
| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | TerrariaPlugin + [ApiVersion(2,1)] | BasePlugin + [PluginMetadata] |
| 死亡记录 | GetDataHandlers.KillMe（UTSL 已删除该事件） | NetPacketHandler.Register<PlayerDeathV2>；e.PlayerId -> args.Packet.PlayerSlot，e.PlayerDeathReason -> args.Packet.Reason |
| 玩家离开 | ServerApi.Hooks.ServerLeave | UnifierApi.EventHub.Netplay.LeaveEvent |
| 进服提示 | ServerApi.Hooks.NetGreetPlayer（UTSL 没有） | TShockAPI.Hooks.PlayerHooks.PlayerPostLogin |
| 世界进度 | NPC.downedBoss3 / downedPlantBoss | server.NPC.downedBoss3 / downedPlantBoss（按世界） |
| NPC 数组 | Main.npc | server.Main.npc |
| 配置 | Newtonsoft.Json，tshock/BetterBack.json | System.Text.Json，config/BetterBack/BetterBack.json |
| 命令执行者 | args.Player | args.Player ?? args.ExecutorActor（控制台执行时为 null） |

> ⚠️ 死亡点是内存态：上游本身就没有任何持久化（DataManager 只用一个 ConcurrentDictionary），服务器重启后死亡点会全部丢失。若要重启后保留，需要在 DataManager 里接 linq2db 落库，属于功能增强。

## 插件版本
### v2026.5.2.0
- 初始版本发布
- 支持死亡点记录和传送
- 支持自定义 Buff 和无敌时间
- 支持自动返回功能
- 支持地牢 / 神庙死亡点限制

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
