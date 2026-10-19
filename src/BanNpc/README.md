# BanNpc 禁止怪物生成
- **作者**: Patrikk、GK（UTSL 移植：星梦）
- **版本**: v1.0.0

## 功能概述
- 按 NPC ID 列表阻止指定怪物生成
- 生成时直接置 `active=false`，不产生尸体/掉落
- 指令动态增删，配置即时保存

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/bm list` | `bannpc.use` | 查看阻止表 |
| `/bm add <名称或ID>` | `bannpc.use` | 添加 |
| `/bm del <名称或ID>` | `bannpc.use` | 移除 |

## 配置
配置文件路径：`config/BanNpc/settings.json`

```json
{
  "阻止怪物生成表": [4, 50]
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 阻止怪物生成表 | 数组 | [] | 被阻止的 NPC ID（netID） |

## 说明
- 只拦**生成**，不清理已经存在的怪物
- UTSL 的 `NpcTransform` 没有对应钩子，NPC 变形不会被拦截（会按变形后的新 ID 重新判断）
- 日志关键词：`[BanNpc]`

## 插件版本
### v1.0.0
- UTSL 移植：LazyPlugin → BasePlugin；`NpcSpawn` → `HookEvents.Terraria.NPC.OnSpawn`
- 权限节点 `bannpc.use`

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
