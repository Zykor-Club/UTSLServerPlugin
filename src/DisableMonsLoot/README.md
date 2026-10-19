# DisableMonsLoot 禁怪物掉落
- **作者**: 羽学（UTSL 移植：星梦）
- **版本**: v1.3.5

## 功能概述
- 击杀指定 Boss 后，清掉它掉落的战利品（按范围 + 物品 ID 白名单）
- 按 Boss 分组配置：每组可以单独开关、设置自己的物品列表
- 支持"全杀"模式：杀满该组所有 Boss 后才关闭清理

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/kdm` | `disablemonsloot.admin` | 菜单 |
| `/kdm on` | `disablemonsloot.admin` | 开关插件 |
| `/kdm list` | `disablemonsloot.admin` | 查看清理列表 |
| `/kdm kill` | `disablemonsloot.admin` | 开关全杀模式 |
| `/kdm f <格>` | `disablemonsloot.admin` | 设置清理范围 |
| `/kdm reset` | `disablemonsloot.admin` | 恢复所有清理开关 |

## 配置
配置文件路径：`config/DisableMonsLoot/settings.json`

```json
{
  "插件开关": true,
  "是否全杀": false,
  "清理范围": 35,
  "清理列表": [
    { "怪物名": "史莱姆王", "清理": true, "怪物ID": [50], "物品ID": [3318, 2430, 256] }
  ]
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 插件开关 | 布尔值 | true | 总开关 |
| 是否全杀 | 布尔值 | false | 关闭时击杀组内 1 只即停；开启时杀满整组 |
| 清理范围 | 整数 | 35 | 以 Boss 死亡点为中心的范围（格） |
| 清理列表 | 数组 | 内置几只 Boss | 每项含 怪物名 / 清理 / 怪物ID / 物品ID |

## 说明
- 只清理**范围内**且**物品 ID 命中**的地面掉落物；不碰背包
- 每个世界的击杀进度独立记录（服务端按世界名分组）
- 清怪用的是 `NPC.checkDead`（每次受伤都会触发），已用 `npc.life <= 0` 过滤
- 清理用 `WorldItem.TurnToAir(server)`，由服务端负责同步消失
- 日志关键词：`[DisableMonsLoot]`

## 插件版本
### v1.3.5
- UTSL 移植：TerrariaPlugin → BasePlugin；NpcKilled → `HookEvents.Terraria.NPC.checkDead`
- 多世界适配：按 `ServerContext` 读写物品、发 despawn 包；进度按世界隔离
- 权限节点 `disablemonsloot.admin`

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
