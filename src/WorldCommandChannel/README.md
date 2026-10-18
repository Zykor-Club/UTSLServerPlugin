# WorldCommandChannel 按世界指令通道
- **作者**: 星梦
- **版本**: v1.0.0

## 功能概述
- 为外部多世界管理器提供「按世界执行指令」的 REST 能力
- 提供世界列表（名称、尺寸、是否运行）
- 可对指定世界、按序号或全部世界执行指令
- 执行语义与在控制台敲完全等价
- 返回格式与 TShock rawcmd 相同，便于管理器复用解析逻辑

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| （无聊天指令） | - | 只注册 REST 路由 `/world/list` 与 `/world/command` |

## 配置
配置文件路径：无（没有配置文件）

```json
{}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| （无） | - | - | 本插件没有配置项 |

## 说明
UTSL 是单进程多世界，而 TShock 自带的 /v3/server/rawcmd 只能落到默认世界：外部管理器无法对指定世界发指令。本插件补上这个能力，纯插件实现（用 TShock.RestApi.Register），不改 UTSL 核心。

## 接口
| 方法 | 路径 | 说明 |
|:--|:--|:--|
| GET | /world/list | 世界列表（序号、名称、是否运行、宽高） |
| POST | /world/command?world={名称\|序号\|all}&cmd={指令} | 对指定世界执行指令 |

- world 省略或为 all → 对所有正在运行的世界执行
- 指令自动补前导 /（与 TShock rawcmd 约定一致）
- 返回格式：`{"status":"200","world":...,"count":...,"response":[...]}`，找不到世界时返回 404
- 需要令牌对应的用户组带 restapi 权限（superadmin 默认有）

## 示例
~~~
GET  http://127.0.0.1:7878/world/list?token=<令牌>
POST http://127.0.0.1:7878/world/command?token=<令牌>&world=Dev&cmd=/save
POST http://127.0.0.1:7878/world/command?token=<令牌>&cmd=/save          # 全部世界
POST http://127.0.0.1:7878/world/command?token=<令牌>&world=2&cmd=/help  # 按序号
~~~

## 实现要点
- 执行语义与在控制台敲完全等价：new CommandExecutor(目标世界, UserId: byte.MaxValue, TSRestPlayer) —— UTSL 的「服务器权限 + 指定世界」
- CommandExecutor.SourceServer 是 UTSL 内置的「这条指令属于哪个世界」字段
- 序号按运行时正在运行的世界列表（从 1 开始）选取

## 依赖
- UTSL（UnifierTSL）
- TShockAPI（随 UTSL 提供）

## 插件版本
### v1.0.0
- 首次发布
- 提供 /world/list 与 /world/command

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
