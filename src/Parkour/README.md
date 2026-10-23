# Parkour 跑酷
- **作者**: 星梦
- **版本**: v0.1.0（契约 v1）
- **类型**: `MiniGameCore` 模块族玩法（`[assembly: RequiresCoreModule("MiniGameCore")]`）

## 功能概述
- **两种地图来源**：空白世界（自动找空地铺起点平台）或专用地图（`worldFile` 指向现成 `.wld`，一格不改）
- **区域全由作者在游戏里圈**：活动区 / 起点 / 终点 / 多个存档点，存成 `config/Parkour/maps/<id>.json`
- **两角选点**：`/pk p1` `/pk p2` 圈矩形（不是只能画圆/正方形），也支持以自身为中心 `±半径`
- **硬校验**：起点/终点/存档点只要有一点落在活动区外就**拒绝保存**（不会写进 JSON）
- **倒计时**：3~5 秒（按地图），期间只能在起点区域内活动，出界推回最近的合法格
- **起点出生点**：在起点区域内自动找 3×3 空地（脚下实心、上方三格空），找不到回退矩形中心
- **存档点**：只记不倒退（`progress` 只增），同时改写玩家重生点
- **死亡/越界回传**：死亡复活与走出活动区都会被送回“上一个存档点（没有则起点）”
- **终点判定**：进入矩形即完赛，幂等；**不要求集齐存档点**（抄近路算技巧）
- **区域显示**：照 RegionView 的做法（临时假图格 → 只发给该玩家 → 立刻还原），默认开启、配置可关

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/pk` | `parkour.play` | 帮助 |
| `/pk maps` | `parkour.play` | 地图清单（尺寸 + 起点/终点/存档点/活动区完成度） |
| `/pk solo <图>` | `parkour.play` | 单人开跑（房间世界 = 地图世界） |
| `/pk leave` | `parkour.play` | 离开房间/建造世界 |
| `/pk build [图]` | `parkour.admin` | 进入建造世界（未设起点时是虚空 + 中间石台） |
| `/pk newmap <id> <世界文件名> [显示名]` | `parkour.admin` | 用现成 `.wld` 建一张图（id 限小写字母/数字/_/-） |
| `/pk p1` / `/pk p2` | `parkour.admin` | 记下选区两角（会显示包围盒尺寸） |
| `/pk sel` / `/pk sel clear` | `parkour.admin` | 查看 / 清空选区 |
| `/pk here start` | `parkour.admin` | 起点判定领域（出生点取矩形中心） |
| `/pk here spawn` | `parkour.admin` | 只改起点出生点 |
| `/pk here finish` | `parkour.admin` | 终点判定领域 |
| `/pk here save <序号> [半径]` | `parkour.admin` | 第 N 个存档点（重生点取矩形中心） |
| `/pk here activity [半径]` | `parkour.admin` | 活动区（越界范围） |
| `/pk show` | `parkour.admin` | 查看当前地图所有区域 |
| `/pk clear <start|spawn|finish|saves|activity|all>` | `parkour.admin` | 清掉某个区域 |
| `/pk set <countdown|time|grace|capacity> <值>` | `parkour.admin` | 倒计时 / 时限 / 宽限 / 容量 |
| `/pk prep` | `parkour.admin` | 预热空白世界（不传送玩家） |

## 配置
配置文件路径：`config/Parkour/settings.json`

```json
{
  "赛季": "s1",
  "区域显示": true,
  "区域显示_刷新秒": 2,
  "区域显示_范围格": 96
}
```

## 配置说明
| 键 | 默认 | 说明 |
|------|:----:|------|
| `赛季` | s1 | 写进成绩的赛季标识 |
| `区域显示` | true | 是否给房间里的玩家画区域边界（只对他自己可见） |
| `区域显示_刷新秒` | 2 | 刷新间隔（最小 1 秒） |
| `区域显示_范围格` | 96 | 以玩家为中心的可视范围（16~400） |

## 地图文件
路径：`config/Parkour/maps/<id>.json`

```json
{
  "id": "ninja",
  "name": "忍者审判",
  "worldFile": "ParkourNinja",
  "capacity": 1,
  "timeLimitSeconds": 900,
  "graceSeconds": 60,
  "countdownSeconds": 5,
  "activity": [4149, 261, 4376, 616],
  "start":    [4162, 575, 4214, 613],
  "startSpawn": [4188, 594],
  "finish":   [4300, 575, 4350, 613],
  "saves": [ { "index": 1, "x0": 4216, "y0": 575, "x1": 4240, "y1": 613, "spawnX": 4228, "spawnY": 594 } ],
  "yTolerance": 6,
  "scoreBase": 1000,
  "scorePerSecond": 5,
  "scorePerSave": 20
}
```

## 说明
- **世界尺寸必须与玩家所在世界一致**：1.4.5.8 的无缝转移要求两边尺寸完全相同（跨尺寸会被引擎拒绝）
- **专用地图不清空**：`worldFile` 指向的图一个方块都不改；空白图才铺起点平台
- **世界出生点**：进房时设为起点中心并落盘（客户端多世界切换不会重发世界头，只改内存不一定生效）
- 房间复用：每局开始会清空上一局进度/完赛记录，否则第二局不会判定
- 区域显示是**客户端侧假图格**，不会写入 `.wld`

## 插件版本
### v0.1.0
- 首版：两角选区与圈区域指令、活动区校验、倒计时区域锁、3×3 出生点、存档点与死亡回放、越界传回、区域可视化

## 反馈
- 共同维护的插件库：https://github.com/Zykor-Club/UTSLServerPlugin