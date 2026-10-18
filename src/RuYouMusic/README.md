# RuYouMusic 点歌机（服务端）
- **作者**: 星梦
- **版本**: v1.0.0

配套管理器插件：`UTSLManager.Avalonia/extensions/RuYouMusicPanel`
配套客户端插件见仓库 `src/RuYouMusic/client`

## 功能概述

- 搜索（网易云，走 https://music-api.gdstudio.xyz/api.php）、队列、自动下一首、中途加入的进度对齐
- 把「播放/停止/音量」下发给装了客户端插件的玩家；客户端自己下载音频并播放（不占服务器带宽）
- 维护「谁装了客户端插件」名单；REST 与游戏内指令都可驱动

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| `/music search <关键字>` | `ruyoumusic.use` | 搜索 |
| `/music add <id\|关键字>` | `ruyoumusic.use` | 点歌入队 |
| `/music list` | `ruyoumusic.use` | 查看队列 |
| `/music skip` | `ruyoumusic.use` | 切歌 |
| `/music stop` | `ruyoumusic.use` | 停止 |
| `/music vol <0-100>` | `ruyoumusic.use` | 音量 |
| `/music clients` | `ruyoumusic.use` | 装了客户端插件的玩家名单 |
| GET `/music/status` | `restapi` | 播放状态、进度、时长、音量、队列数、装插件人数 |
| GET `/music/clients` | `restapi` | 在线玩家 + 是否装插件（hasPlugin/version/lastSeenMs/positionMs） |
| GET `/music/queue` | `restapi` | 当前曲 + 队列 |
| GET `/music/lyrics` | `restapi` | 当前曲歌词（lines: [{ms,text}]）+ 实时进度 |
| GET `/music/search?q=&count=` | `restapi` | 搜索 |
| GET `/music/enqueue?id=` 或 `?q=&by=` | `restapi` | 点歌（id 优先；否则用关键字搜第一首） |
| GET `/music/skip` `/music/stop` `/music/clear` `/music/remove?index=` | `restapi` | 切歌 / 停止 / 清空队列 / 移除某一首 |
| GET `/music/volume?pct=0-100` | `restapi` | 音量（同步给所有客户端） |
| GET `/music/config?on=1&off=1&autoNext=1&maxQueue=&searchCount=&probeSeconds=` | `restapi` | 配置读写 |

> `/music` 的别名：`/点歌`；REST 需要令牌对应组带 `restapi` 权限。

## 配置
配置文件路径：`config/RuYouMusic/settings.json`

```json
{
  "enabled": true,
  "maxQueue": 20,
  "searchCount": 5,
  "probeSeconds": 15,
  "autoNext": true,
  "volumePercent": 100,
  "sources": ["netease"],
  "apiBase": "https://music-api.gdstudio.xyz/api.php",
  "apiExtra": "",
  "apiHeaders": "",
  "apiSign": 0,
  "commandPermission": "ruyoumusic.use"
}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| enabled | 布尔值 | true | 是否启用点歌机 |
| maxQueue | 整数 | 20 | 队列长度上限 |
| searchCount | 整数 | 5 | 搜索返回条数 |
| probeSeconds | 整数 | 15 | 探测客户端插件的时间间隔（秒） |
| autoNext | 布尔值 | true | 当前曲播完后自动下一首 |
| volumePercent | 整数 | 100 | 服务端下发音量（0-100，客户端还会乘游戏内音乐音量） |
| sources | 数组 | ["netease"] | 取链音源，按顺序尝试 |
| apiBase | 字符串 | music-api.gdstudio.xyz | 取链服务地址（可替换为自建代理） |
| apiExtra | 字符串 | 空 | 追加到每个请求末尾的原始串（如自有 token 参数） |
| apiHeaders | 字符串 | 空 | 形如 `Cookie=a=b;Referer=...`，用 `;` 分隔 |
| apiSign | 整数 | 0 | 0=不签名；1=按前端约定追加 `&s=crc32(签名对象)` |
| commandPermission | 字符串 | ruyoumusic.use | `/music` 所需权限 |

## 说明

### 协议（与客户端插件）

走 Terraria 网络包，**帧格式** `[长度:2 LE][类型:1][载荷:256]`（固定 260 字节帧）。接收侧 `RawData` 不含长度头，所以 `RawData[0]` 是类型、payload[i] 对应 `RawData[1+i]`。

载荷内部（两端一致）：

| 偏移 | 字节 | 含义 |
|:--|:--|:--|
| [0..3] | 4 | 魔数 `RYM1`（0x52 0x59 0x4D 0x31） |
| [4] | 1 | cmd |
| [5] | 1 | 协议版本（当前 1） |
| [6..13] | 8 | 会话令牌（C→S 由客户端回带；S→C 里是服务端给该玩家的令牌） |
| [14..15] | 2 | 保留 |
| [16..] | … | 各 cmd 的数据 |

**包类型（Terraria message id）**

| 方向 | id | 用途 |
|:--|:--|:--|
| S→C | 129 | PROBE：探测该客户端是否装了插件（原版客户端对该 id 无处理，会安全忽略） |
| S→C | 67 | SESSION / PLAY / STOP / VOLUME（只发给已握手的客户端，避免原版误解析自定义载荷） |
| C→S | 15 | HELLO / FINISHED / POSITION |

**cmd 一览**

| cmd | 方向 | 数据 |
|:--|:--|:--|
| 1 | S→C | PROBE（无数据） |
| 5 | S→C | SESSION：[16..23] = 8 字节令牌 |
| 2 | S→C | PLAY：[16..23] trackId(int64)、[24..27] startOffsetMs(int32)、[28..31] durationMs(int32)、[32] volumePercent(byte)、[40..] 两个字符串（songName、artist；每个 = [u16 长度][UTF-8]） |
| 3 | S→C | STOP |
| 4 | S→C | VOLUME：[16] = 0..100 |
| 1 | C→S | HELLO：[5] = 客户端版本 |
| 3 | C→S | FINISHED：[16..23] = trackId（放完了） |
| 4 | C→S | POSITION：[16..23] = trackId、[24..27] = positionMs |

**客户端插件要做的**（按加载器 `R.PluginSwitch` 的 ABI）：

1. `[Plugin(id,name,author,desc,ver)]` 类 + 静态构造函数里 `GameHooks.NetGetData.Register(OnNetGetData)`
2. 收到 id **129** → 回 HELLO（带令牌位留空）；收到 id **67** 且魔数为 RYM1：SESSION 存令牌、PLAY 拉音频播放、STOP 停、VOLUME 调音量；处理完把 `e.Handled = true` 吃掉原版处理
3. 播放：请求 `types=url&source=netease&id=<trackId>&br=320` 取直链 → 下载到 `MusicCache/<id>.mp3`（缓存命中直接播）→ **MCI（winmm.mciSendString）** 播放；`play rymusic from <startOffsetMs + 本地已过毫秒>` 实现中途加入对齐
4. 每 ~2 秒回 POSITION 给服务端（管理器歌词靠它精确同步）；MCI 由 playing 变 stopped 时回 FINISHED
5. 歌词可自己拉 `types=lyric` 显示（也可以只依赖服务端面板显示）

### 安全说明

握手用「服务端下发的一次性随机令牌」而不是作者那套 RSA 公私钥：两端都是你自己的，随机令牌足够防伪造，且服务端不需要保存任何私钥。

### 构建与部署

```
dotnet build src/RuYouMusic/RuYouMusic.csproj -c Release
copy out\Release\RuYouMusic.dll <utsl-deploy>\plugins\
```

### 排障与已知坑（都是实测踩过的）

1. **帧内偏移**：帧是 `[长度:2][类型:1][载荷:256]`，所以**载荷偏移 N = 帧偏移 3+N**。
   第一版把 PLAY/VOLUME 数据直接写在 `frame[16]`（少了 3），客户端按载荷偏移解析就整体错位：
   trackId 变垃圾、音量读成 0、歌名从 UTF-8 中间开始读（「晴天」显示成「天」）并把歌手/直链一起吞掉。
   现在统一用 `FrameDataOffset = 3 + PayloadOffset`。
2. **重复 PLAY 竞态**：登录探测与周期探测若撞在一起，客户端会回两次 HELLO → 收到两次 SESSION/PLAY，
   于是**二次下载同一个文件**并互相覆盖/删除，表现为「播了几秒突然失败说没拿到直链」。
   服务端：登录探测后记 `ProbeAt`，不再立刻重探；客户端：同一首已在下载/播放时忽略重复 PLAY，
   且只有「失败的是当前曲且当前没在播」才删文件并上报 FINISHED。
3. **接口不稳**：`music-api.gdstudio.xyz` 会间歇 503 或返回 HTML 错误页；VIP/地区受限歌返回空直链。
   对策：解析带重试 + 多候选 + **同名可播放版本兜底**；`/music/search?check=1` 可标注能否播放。
   joox 音源实测 `types=url` 一律返回空（`br=-1`），已从默认音源去掉。
4. **客户端音量** = 服务端音量 × 游戏内「音乐」音量；游戏里音乐音量为 0 时听不到。
5. 客户端日志：`<游戏目录>\MusicCache\_client.log`（含解析出的直链、MCI 返回码、进度上报），排障先看它。

## 插件版本
### v1.0.0
- 首次发布：搜索 / 队列 / 下单下发 / 客户端播放 / 歌词 / 管理器面板
- 协议 v2：PLAY 帧末尾追加直链 URL；帧长 1024（原来 260 会截断长直链）

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
