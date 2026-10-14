# RuYouMusicClient（点歌机 · 客户端插件）

由游戏目录里的 **ReLogic.dll（改版引导）+ R.PluginSwitch（加载器）** 加载，放在 `<游戏根目录>\Plugins\` 下。
协议见上一级 README（与 RuYouMusic 服务端插件配套）。

## 构建（需要游戏目录）

```powershell
# 0) 准备 R.PluginSwitch.dll 作为编译引用：它随作者插件包以 .rym 形式分发，
#    可先用 zlib wbits=-15 解压成本目录能引用到的 .dll（见 ../README 或逆向分析文档）
# 1) 编译
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Terraria"
# 2) 可选：压成 .rym（体积更小；直接放 .dll 也能被加载）
powershell -File .\pack-rym.ps1
```

产物：`bin\Release\RuYouMusicClient.dll`（或 `RuYouMusicClient.rym`）→ 复制到 `<游戏根目录>\Plugins\` → 重启游戏。

## 不带游戏目录的自检

```powershell
cd selfcheck
dotnet build -c Release     # 用桩类型编译同一份源码，验证语法/类型（不需要游戏、不需要 NuGet）
```

## 行为

1. 收到服务端 **129** 探测 → 回 HELLO；收到 **67**：SESSION 存令牌 / PLAY 下载并播放 / STOP / VOLUME
2. 播放：请求 `types=url` 取直链 → 下载到 `MusicCache\<id>.mp3`（命中缓存秒播）→ **MCI（winmm）** 播放；`play rymusic from <offset>` 支持中途加入对齐
3. 每 ~2 秒回 POSITION；MCI 由 playing 变 stopped 时回 FINISHED（服务端据此自动下一首）
4. 下载文件头是 MZ（可执行）直接拒绝；缓存超过 120 首自动清理最旧的
5. 音量 = 服务端音量 × 游戏内音乐音量；进出主菜单自动复位
