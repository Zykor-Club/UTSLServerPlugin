# CGive 离线 give
- **作者**: Leader、肝帝熙恩（UTSL 移植：星梦）
- **版本**: v1.0.1

## 功能概述
- 给离线/在线玩家预存一条命令，玩家**下次登录时自动执行**
- 支持 `server`（控制台身份）或指定在线玩家作为执行者
- `-1` 表示所有玩家各执行一次（发过不再发）
- 数据存 `CGive` / `Given` 两张数据库表（与上游一致）

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/cgive add <执行者\|server> <玩家名\|-1> <命令>` | `cgive.admin` | 添加 |
| `/cgive list` | `cgive.admin` | 列出待执行 |
| `/cgive del <id>` | `cgive.admin` | 删除 |
| `/cgive reset` | `cgive.admin` | 清空全部 |

## 配置
配置文件路径：无（数据全部在数据库表 `CGive` / `Given`）

```json
{}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| （无） | - | - | 数据表：`CGive(ID, executer, cmd, who)`、`Given(Name, Id)` |

## 说明
- 命令里的 `{name}` 会替换成目标玩家名
- 在线目标会立即执行；离线则保存，登录后补发
- 登录执行走 `PlayerPostLogin`；`-1` 记录在"已发放"里，避免重复
- REST `/getWarehouse?name=<玩家>` 已恢复：解析该玩家的 `/give` 离线命令返回仓库列表
- 日志关键词：`[CGive]`

## 插件版本
### v1.0.1
- UTSL 移植：TerrariaPlugin → BasePlugin；SQL 表 → JSON；`NetGreetPlayer` → `PlayerPostLogin`
- 权限节点 `cgive.admin`

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
