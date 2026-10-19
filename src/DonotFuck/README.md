# DonotFuck 聊天敏感词
- **作者**: Cai、羽学（UTSL 移植：星梦）
- **版本**: v3.2.4

## 功能概述
- 玩家聊天命中敏感词时，把词替换成等长星号（大小写不敏感）
- 可记录命中日志（按天分文件），支持指令增删词表
- 管理员与 owner 组豁免

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/df list [页]` | `donotfuck.use` | 查看敏感词 |
| `/df add <词语>` | `donotfuck.admin` | 添加 |
| `/df del <词语>` | `donotfuck.admin` | 删除 |
| `/df log` | `donotfuck.admin` | 开关记录 |
| `/df clear` | `donotfuck.admin` | 清空日志 |

## 配置
配置文件路径：`config/DonotFuck/settings.json`

```json
{
  "每页行数": 30,
  "启用日志": true,
  "脏话表": ["6", "六"]
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 每页行数 | 整数 | 30 | `/df list` 每页条数 |
| 启用日志 | 布尔值 | true | 是否写命中日志 |
| 脏话表 | 数组 | ["6","六"] | 敏感词列表（不区分大小写） |

## 说明
- 命中后**直接改写**聊天内容（保留原广播），不额外刷屏
- 日志位置：`config/DonotFuck/logs/DonotFuck-yyyy-MM-dd.log`
- 跳过以指令前缀开头的消息
- 日志关键词：`[DonotFuck]`

## 插件版本
### v3.2.4
- UTSL 移植：LazyPlugin → BasePlugin；`ServerChat` → `EventHub.Chat.ChatEvent`（可改写文本）
- 权限节点 `donotfuck.use` / `donotfuck.admin`

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
