# AutoTeam 自动分队
- **作者**: 十七、肝帝熙恩（UTSL 移植：星梦）
- **版本**: v2.4.9

## 功能概述
- 玩家登录 / 换队时，按用户组自动分配到指定队伍
- 组 → 队伍映射可配置；带 `noautoteam` 权限的组跳过

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/autoteam`（别名 `/at`） | `autoteam.toggle` | 开关插件 |

## 配置
配置文件路径：`config/AutoTeam/settings.json`

```json
{
  "开启插件": true,
  "组对应的队伍": {
    "guest": "pink",
    "default": "blue",
    "owner": "red",
    "admin": "green",
    "vip": "none"
  }
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 开启插件 | 布尔值 | true | 总开关 |
| 组对应的队伍 | 对象 | 见上 | 组名 → 队伍名（none/red/green/blue/yellow/pink，或中文） |

## 说明
- 登录完成（`PlayerPostLogin`）时分配一次；玩家手动换队会被拉回配置的队伍
- 未配置的组不处理；`noautoteam` 权限可豁免
- UTSL 没有 `NetGreetPlayer`，用登录完成事件代替
- 日志关键词：`[AutoTeam]`

## 插件版本
### v2.4.9
- UTSL 移植：LazyPlugin → BasePlugin；`PlayerTeam` 事件 → `NetPacketHandler<PlayerTeam>`
- 权限节点 `autoteam.toggle`

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
