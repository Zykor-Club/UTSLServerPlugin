# WorldChainSync 世界链同步
- **作者**: 星梦
- **版本**: v1.3.0

## 功能概述
- 把「世界链」（同一张大地图切出来的多个世界）的**世界级状态**保持一致
- 同步内容：时间/月相/血月/日食、天气（雨/风/云/史莱姆雨）、困难模式、Boss 与事件进度、已解救 NPC
- 两条路径：
  - **换乘瞬时**：每 250ms 检测玩家所在世界变化，旧世界状态立刻拷到新世界
  - **周期统一**：每 2s 以"有玩家的世界"为权威，推给链上其它世界（防止时间各自漂移）
- 玩家自身（buff/血量/背包）不需要同步：UTSL 换乘迁移的是同一个 Player 对象

## 与 EdgeStitch 的分工
| 插件 | 职责 |
|:--|:--|
| **EdgeStitch** | 换乘（拓展类）：边缘触发、落点、冷却、世界链 |
| **WorldChainSync** | 同步（同步类）：世界状态一致 |

两者各自读自己的配置；**「世界链」列表建议保持一致**。

## 配置
路径：`config/WorldChainSync/WorldChainSync.json`

```json
{
  "世界链": ["West", "Dev", "East"],
  "检测间隔_毫秒": 250,
  "统一间隔_毫秒": 2000,
  "同步内容": {
    "时间": true,
    "天气": true,
    "血月": true,
    "日食": true,
    "南瓜月": true,
    "霜月": true,
    "史莱姆雨": true,
    "渔夫任务": true,
    "入侵": true,
    "困难模式": true,
    "Boss进度": true,
    "已解救NPC": true
  }
}
```

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| 世界链 | 数组 | ["West","Dev","East"] | 参与同步的世界名（与 config.json 的 autoStartServers.name 一致） |
| 检测间隔_毫秒 | 整数 | 250 | 换乘检测间隔；越小越即时 |
| 统一间隔_毫秒 | 整数 | 2000 | 周期统一的间隔 |
| 同步内容 | 对象 | 全开 | 分组开关，见下表 |

### 同步内容分组（都可单独关）

| 分组 | 默认 | 包含 |
|:--|:--:|:--|
| 时间 | 开 | 时刻/昼夜/月相/月型/星星数/快进到黎明黄昏/**日晷冷却 + 月晷冷却** |
| 天气 | 开 | 雨/雨时长/雨强/风力/云量/云背景 |
| 血月 | 开 | `bloodMoon` |
| 日食 | 开 | `eclipse` |
| 南瓜月 | 开 | `pumpkinMoon` |
| 霜月 | 开 | `snowMoon` |
| 史莱姆雨 | 开 | `slimeRain` / `slimeRainTime` |
| 入侵 | 开 | 入侵类型/规模/起点/延迟 |
| 困难模式 | 开 | `hardMode`（关掉则各世界可独立困难/普通） |
| Boss进度 | 开 | 全部 `downed*` 旗标（含南瓜月/霜月/四柱/火星/海盗等） |
| 已解救NPC | 开 | 哥布林/巫师/机械师/渔夫/发型师/税收官/高尔夫球手/酒保 |
| 渔夫任务 | 开 | `anglerQuest` / `anglerQuestFinished`（世界级：当前委托与是否完成） |

> 说明：`Main.GameMode`（旅程/大师等世界模式）**不再同步** —— 它属于世界属性，被改写会让客户端显示异常。
> **玩家自身属性不需要同步**：UTSL 换乘迁移的是同一个 `Player` 对象，所以渔夫任务完成次数（`anglerQuestsFinished`）、
> 永久加成（Aegis Fruit / Ambrosia / Arcane Crystal / Galaxy Pearl / Gummy Worm 等 `used*` 旗标）、血量/魔力/背包/buff 全部自动跟随。
> 只有**世界级**状态（本插件同步的这些）才需要搬运。
> 沙尘暴相关字段在当前 OTAPI 的 `MainSystemContext` 里不存在，无法同步。

## 插件版本
### v1.3.0
- 时间组新增 **月晷冷却**（`moondialCooldown`，与日晷并列）
- 新增同步组 **渔夫任务**：`anglerQuest` / `anglerQuestFinished`（世界级委托；玩家自己的完成次数属 Player，随换乘自动保留）

### v1.2.0
- 稀有事件再拆分：**血月 / 日食 / 南瓜月 / 霜月** 各自独立开关；史莱姆雨从天气挪到事件组单独开关

### v1.1.0
- 同步内容按分组可开关：时间 / 天气 / 稀有事件 / 入侵 / 困难模式 / Boss进度 / 已解救NPC
- 新增同步：南瓜月、霜月、史莱姆王、独眼巨鹿（downedQueenSlime / downedDeerclops）
- 移除 Main.GameMode 的同步（世界模式不是链内共享状态）
- 沙尘暴字段在当前 MainSystemContext 不存在，暂不支持

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
