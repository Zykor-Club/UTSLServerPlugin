# WorldCommandChannel

按世界执行指令的 REST 通道（供外部多世界管理器使用）。

## 为什么需要它

UTSL 是**单进程多世界**，而 TShock 自带的 `/v3/server/rawcmd` 只能落到默认世界：
外部管理器（如 PGame-TSManager 那类）**无法对指定世界发指令**。
本插件补上这个能力，纯插件实现（用 `TShock.RestApi.Register`），**不改 UTSL 核心**。

## 接口

| 方法 | 路径 | 说明 |
|:--|:--|:--|
| GET | /world/list | 世界列表（名称、尺寸、是否运行） |
| POST | /world/command?world={名称\|序号\|all}&cmd={指令} | 对指定世界执行指令 |

- `world` 省略或为 `all` → **对所有世界执行**
- 指令**自动补前导 `/`**（与 TShock rawcmd 约定一致）
- 返回格式与 TShock rawcmd 相同：`{"status":"200","response":[...]}`，便于管理器复用解析逻辑

## 示例

```
GET  http://127.0.0.1:7878/world/list?token=<令牌>
POST http://127.0.0.1:7878/world/command?token=<令牌>&world=Dev&cmd=/save
POST http://127.0.0.1:7878/world/command?token=<令牌>&cmd=/save          # 全部世界
POST http://127.0.0.1:7878/world/command?token=<令牌>&world=2&cmd=/help  # 按序号
```

## 实现要点

- 执行语义与在控制台敲**完全等价**：
  `new CommandExecutor(目标世界, UserId: byte.MaxValue, TSRestPlayer)` —— UTSL 的「服务器权限 + 指定世界」
- `CommandExecutor.SourceServer` 是 UTSL 内置的「这条指令属于哪个世界」字段
- 需要令牌对应的用户组带 `restapi` 权限（superadmin 默认有）

## 依赖

- UTSL（UnifierTSL）
- TShockAPI（随 UTSL 提供）
