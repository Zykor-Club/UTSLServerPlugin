# AutoAirItem 自动垃圾桶
- **作者**: 羽学（UTSL 移植：星梦）
- **版本**: v1.2.8

## 功能概述
- 玩家把物品扔进垃圾桶位格后，插件学习该类物品并自动清理背包里的同类
- 清理数量会记录，可用 `/air del <ID>` 成批取回
- 支持按玩家开关、自动学习、清理提示、垃圾数量统计
- 玩家数据存 `AutoTrash` 数据库表（与上游一致）

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/air` | `autoair.use` | 菜单 + 当前状态 |
| `/air on` | `autoair.use` | 切换垃圾桶总开关 |
| `/air list` | `autoair.use` | 列出自己的垃圾桶 |
| `/air clear` | `autoair.use` | 清空自己的垃圾表与记录 |
| `/air yes` | `autoair.use` | 把手持物品加入垃圾桶 |
| `/air auto` | `autoair.use` | 切换垃圾桶位格监听 |
| `/air mess` | `autoair.use` | 切换清理提示 |
| `/air add <名字/ID>` | `autoair.use` | 加入垃圾表 |
| `/air del <名字/ID>` | `autoair.use` | 移出并返还已清理的物品 |
| `/air ck <数量>` | `autoair.use` | 统计已清理数量超过该值的玩家 |
| `/airreset` | `autoair.admin` | 清空所有玩家数据 |

## 配置
配置文件路径：`config/AutoAirItem/settings.json`（只存插件开关与排除物品；玩家数据在数据库）

```json
{
  "插件开关": true,
  "排除物品": [71, 72, 73, 74]
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 插件开关 | 布尔值 | true | 总开关 |
| 排除物品 | 数组 | [71,72,73,74] | 命中则整轮不清理（上游语义） |

## 说明
- 玩家数据表：`AutoTrash`（ID/Name/Enabled/Auto/Mess/ItemType/DelItem）
- 清理依赖服务端对背包的权威，需要开启 **SSC**，否则客户端会把物品同步回来
- 垃圾桶位格与背包同一轮扫描；命中排除表会直接结束本轮（上游行为）
- 日志关键词：`[AutoAirItem]`

## 插件版本
### v1.2.8
- UTSL 移植：LazyPlugin → BasePlugin；`PlayerUpdate` → `Game.PostUpdate` 扫描
- 数据由 JSON 改为 `AutoTrash` 数据库表；指令集与上游一致

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
