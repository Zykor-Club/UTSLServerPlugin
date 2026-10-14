# GroupBlacklist 组黑名单
- **作者**: 星梦
- **版本**: v1.1.0

## 功能概述
- 禁止指定用户组进入服务器，支持在线踢出和豁免名单
- 支持 /gb 指令动态管理，无需手动编辑配置文件
- 拦截分三层：客户端上报信息阶段、登录完成后、每 tick 定时扫描
- 豁免玩家列表可单独放行玩家
- 定时扫描按世界分别节流，不会跨世界误踢

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| /gb | groupblacklist.admin | 显示帮助信息 |
| /gb on | groupblacklist.admin | 开启插件功能 |
| /gb off | groupblacklist.admin | 关闭插件功能 |
| /gb add <组名> | groupblacklist.admin | 添加黑名单组 |
| /gb del <组名> | groupblacklist.admin | 移除黑名单组 |
| /gb padd <玩家> | groupblacklist.admin | 添加豁免玩家 |
| /gb pdel <玩家> | groupblacklist.admin | 移除豁免玩家 |
| /gb list | groupblacklist.admin | 查看当前列表和状态 |
| /reload | tshock.cfg.reload | 重载配置文件 |

## 配置
配置文件路径：`config/GroupBlacklist/GroupBlacklist.json`

```json
{
  "插件设置": {
    "启用插件": true,
    "拒绝加入提示信息": "你的用户组被禁止进入此服务器",
    "踢出提示信息": "你所属的用户组已被列入黑名单",
    "检测间隔(秒)": 10,
    "是否踢出在线黑名单玩家": true,
    "是否记录日志": true
  },
  "黑名单组列表": ["poooo", "如悠"],
  "豁免玩家列表": []
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 插件设置 | 对象 | 见下 | 插件行为设置 |
| 插件设置.启用插件 | 布尔值 | true | 插件总开关 |
| 插件设置.拒绝加入提示信息 | 字符串 | 你的用户组被禁止进入此服务器 | 拦截玩家时显示的提示 |
| 插件设置.踢出提示信息 | 字符串 | 你所属的用户组已被列入黑名单 | 在线踢出时显示的提示 |
| 插件设置.检测间隔(秒) | 整数 | 10 | 定时扫描的节流间隔 |
| 插件设置.是否踢出在线黑名单玩家 | 布尔值 | true | 是否把在线期间被改组的玩家踢出 |
| 插件设置.是否记录日志 | 布尔值 | true | 是否记录管理操作日志 |
| 黑名单组列表 | 数组 | ["poooo","如悠"] | 被禁止进入的用户组 |
| 豁免玩家列表 | 数组 | [] | 允许进入的玩家名单 |

## 拦截分层
| 层 | 挂载点 | 时机 | 效果 |
|:--|:--|:--|:--|
| 1 | EventHub.Netplay.ReceiveFullClientInfoEvent | 客户端刚上报名字 / UUID，尚未选服、未下发世界数据 | 直接断线，地图都不会加载，并显示「拒绝加入提示信息」 |
| 2 | TShockAPI.Hooks.PlayerHooks.PlayerPostLogin | 登录完成后 | 兜底，用运行时用户组再判一次 |
| 3 | EventHub.Game.PostUpdate 定时扫描 | 每 tick，按「检测间隔(秒)」节流 | 处理在线期间被改组的玩家 |

> 第 1 层是 UTSL 移植版新增的（上游 TShock 只在登录后和定时扫描里拦）。上游那两层都要求 player.IsLoggedIn，未登录的 guest 玩家在上游根本拦不住；第 1 层按"注册账号所属组"判断（未注册玩家按 guest），把 guest 拉黑就能挡住陌生人。
>
> 不用 ServerCheckPlayerCanJoinIn 的原因：UTSL 在 UnifiedServerCoordinator.cs:244 用 out _ 丢掉了 FailReason，全部服务器拒绝时客户端只会看到通用提示，自定义提示语会失效。
>
> ⚠️ 没有玩家在线时世界不 tick（Game.PostUpdate 每帧事件不触发），第 3 层的"在线踢人"只在有人在线时生效 —— 这恰好是它该生效的场合。

## UTSL 移植说明
| 项 | TShock 版 | UTSL 版 |
|:--|:--|:--|
| 入口 | TerrariaPlugin + [ApiVersion(2,1)] | BasePlugin + [PluginMetadata] |
| 定时扫描 | ServerApi.Hooks.GameUpdate | UnifierApi.EventHub.Game.PostUpdate（带 ServerContext） |
| 配置目录 | tshock/ | config/GroupBlacklist/ |
| 日志 | TShock.Log.Warn | TShock.Log.Warning（Warn 在 UTSL 已移除） |
| 命令执行者 | 控制台执行时 args.Player = TSPlayer.Server | 控制台执行时 args.Player 为 null，已回退到 args.ExecutorActor |

## 插件版本
### v1.1.0 (2026-08-12)
- 新增 /gb 指令集，支持动态管理黑名单和豁免名单
- 新增「启用插件」配置项，支持 /gb on/off 开关
- 新增 /gb list 查看当前状态

### v1.0.0 (2026-04-05)
- 初始版本发布

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
