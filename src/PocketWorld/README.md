# PocketWorld · 尘歌壶

> 状态：**v0.3.0 —— 按需创建 + 闲置销毁**（不再固定世界池）
> 分类：UTSL 拓展类

## 它做什么

每个玩家一个**隔离的私人世界**（《原神》尘歌壶）：
- 只有自己（或受邀者/管理员）能进；
- 与主世界完全隔离；
- 世界文件按玩家 **UUID** 持久化 → 壶被销毁后重进，仍是**他自己的那个世界**。

## 架构（v0.3.0）

```
/pocket 进入：
  1. 按 UUID 查这个玩家的壶；没有则【按需创建】一个 ServerContext（上限 = 最大同时存在数）
  2. 等世界就绪 → 在世界线程上 EnsureBlankWorld + SwitchWorld 成"他的世界" + BuildSpawnPlatform
  3. 在【源世界线程】上 TransferPlayerToServer 把人搬进去

/pocket exit：在【当前世界线程】上搬回原世界（壶保留，等待闲置销毁）

闲置销毁（每 30 秒检查一次）：
  壶里没人 且 空闲超过【闲置销毁_秒】→ 存档 → Close() → RemoveServer()  ← 【真正销毁】
```

**为什么敢真正销毁**：UTSL 侧已修复"世界关不掉"的内存泄漏（静态 `Gameplay` 组件 /
`ConditionalDialogue` / `AppContext` / 控制台输入线程 / `ArmorSetBonuses` / MidiPlayer 绑定），
实测 **12 个世界只涨 1.1MB**（修复前 +54.3MB/世界）。

## 配置（`config/PocketWorld/settings.json`）

| 配置项 | 默认 | 说明 |
|:--|:--|:--|
| `启用` | true | 总开关 |
| `最大同时存在数` | 6 | 内存保护（每个壶约 100~170MB）；达上限后拒绝新开 |
| `闲置销毁_秒` | 600 | 壶里没人多久后销毁；**0 = 不销毁**（长期保留） |
| `指令名` | pocket | 注册 `/<名>`、`exit`、`status`（别名 `/cg`） |
| `进入权限` | 空 | 留空 = 所有人 |

## 指令

| 指令 | 说明 |
|:--|:--|
| `/pocket`（或 `/cg`） | 进入自己的尘歌壶 |
| `/pocket exit` | 离开并返回原世界 |
| `/pocket status` | 查看当前所有壶 |

## 关键约束（血泪总结）

1. **壶世界必须与主世界同尺寸** —— `TransferPlayerToServer` 有硬检查（`UnifiedServerCoordinator.cs:872`），
   尺寸不等会**静默 return**。本项目用"主世界的 maxTilesX/Y + 环境变量 `UTSL_CUSTOM_HEIGHT`"来构造。
2. **不生成世界，用空白世界** —— 生成一个 4200x2400 原版世界要几十秒；`EnsureBlankWorld` 写出+加载约 **0.3+0.5 秒**。
3. **壶世界不要控制台输入线程** —— `SupportsConsoleInput => false`，否则该线程会永久阻塞或空转吃满一个 CPU 核心。
4. **状态只在"真正搬人成功"后才提交**（`Pending` → `Inside`），否则加载中重复操作会把玩家卡住。
5. **玩家标识用 `TSPlayer.UUID`**（未登录也有），不是 `Account?.UUID`。

## 部署

```powershell
dotnet build src/PocketWorld/PocketWorld.csproj -c Release
Copy-Item out/Release/PocketWorld.dll <utsl-deploy>\plugins\PocketWorld.dll -Force
Copy-Item out/Release/PocketWorld.dll <utsl-deploy>\plugins\PocketWorld\PocketWorld.dll -Force
```
> ⚠️ 宿主会扫**扁平与一层子目录**，两处都放并保持同版本；输出目录是 `UTSLServerPlugin\out\Release\`。

## 待办

- [ ] `/pocket admin join <玩家名>`：管理员进入某玩家的壶（参照服主的 `/pw admin join`）
- [ ] 世界唤醒接力：跨世界继承"绝对时间与进度"
- [ ] 转移后小地图区块恢复（参照服主的 `SyncMinimap`）
- [ ] 壶的世界名与玩家改名同步（现在进入时确定名字）
