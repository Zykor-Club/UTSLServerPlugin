# ConsoleSql 控制台 SQL
- **作者**: Cai（UTSL 移植：星梦）
- **版本**: v1.0.4

## 功能概述
- 在**控制台**执行任意 SQL，直接操作 TShock 数据库
- 查询结果按表格打印到控制台；非查询语句返回影响行数与耗时
- 玩家执行会被拒绝（仅控制台可用）

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/sql <SQL 语句>` | `consolesql.use` | 执行 SQL（仅控制台） |

## 配置
配置文件路径：无（本插件没有配置项）

```json
{}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| （无） | - | - | 无配置 |

## 说明
- 只建议在控制台/REST 控制台通道使用；不要给玩家组授予 `consolesql.use`
- 危险操作（DROP/DELETE/UPDATE）没有二次确认，执行前先备份数据库
- SQLite 列表：`SELECT name FROM sqlite_master WHERE type='table';`；MySQL：`SHOW TABLES;`
- 日志关键词：`[ConsoleSql]`

## 插件版本
### v1.0.4
- UTSL 移植：TerrariaPlugin → BasePlugin + PluginMetadata
- 权限节点改为 `consolesql.use`；仅控制台可执行

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
