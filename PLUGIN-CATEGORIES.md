# UTSL 插件分类规范

> 建议的插件归类方式（星梦 2026-10-10 提出），用于仓库组织、文档索引和后续开发定位。

## 一、四类定义

| # | 分类 | 定位 | 判断标准 |
|:--|:--|:--|:--|
| **1** | **UTSL 修复插件** | 修复 UTSL 自身的漏洞/缺失 | 解决的问题本该由 UTSL 核心负责，插件只是补上 |
| **2** | **同步类** | 同步世界间、玩家间的数据 | 核心行为是搬运/对齐状态（世界链状态、玩家位置、背包、时间、进度等） |
| **3** | **UTSL 拓展类** | 拓展 UTSL 的功能与玩法 | 核心是利用多世界能力做出新玩法（无缝换乘、多世界重置、整链地图渲染） |
| **4** | **功能类** | 常规服务器功能 | 与多世界无关，单世界也能用的通用功能（指令、提示、管理） |

**判定顺序**：先问"这是不是在补 UTSL 的坑" → 1；再问"是不是在搬数据" → 2；再问"是不是靠多世界才成立" → 3；否则 → 4。

## 二、现有插件归类

### 1. UTSL 修复插件
| 插件 | 修复了什么 |
|:--|:--|
| （待补） | 目前多为直接改 UTSL 源码，未做成插件：自定义世界长度/高度推导（LauncherSettingValues + IWorldDataProvider）、世界尺寸与文件不一致时的容错、渲染边缘格越界容忍（已内联在 GenerateMap 里） |

> 说明：这类问题越小越应该直接进 UTSL 核心；只有"临时补丁"性质、或需要按服务器策略开关的，才做成插件。

### 2. 同步类
| 插件 | 同步内容 |
|:--|:--|
| **EdgeStitch** | 换乘时的世界状态同步（62 个字段：时间/月相/血月/入侵/boss 进度等，每 2 秒 UnifyChain 对齐一次） |

### 3. UTSL 拓展类
| 插件 | 拓展内容 |
|:--|:--|
| **EdgeStitch** | 多世界边缘无缝换乘（把多张同尺寸世界拼成一张连续大地图） |
| **AutoResetPlus** | 整链重置（生成新源地图 → 重新切分 → 替换所有切片 → 重启） |
| **GenerateMap** | 多世界独立出图 + 按世界链横向拼接成一张大地图（验收/宣传用） |
| （UTSLStitchTool） | 不是插件，是配套命令行工具（切分/裁剪/分析 .wld） |

### 4. 功能类
| 插件 | 功能 |
|:--|:--|
| **BetterBack** | /back 返回死亡点/传送点 |
| **BossCommandExecutor** | BOSS 相关指令执行 |
| **CommandTeleport** | 传送指令 |
| **CustomDeathMessages** | 自定义死亡提示 |
| **GroupBlacklist** | 用户组黑名单 |
| **ItemHeldMessage** | 手持物品广播 |
| **ItemPool** | 物品池 |
| **MidiPlayer** | MIDI 音乐播放 |

## 三、建议的目录结构

~~~
UTSLServerPlugin/
├── src/
│   ├── Fixes/            # 1. UTSL 修复插件
│   ├── Sync/             # 2. 同步类
│   ├── Extensions/       # 3. UTSL 拓展类
│   │   ├── EdgeStitch/           # （若侧重换乘玩法放这里）
│   │   ├── AutoResetPlus/
│   │   └── GenerateMap/
│   ├── Features/         # 4. 功能类
│   │   ├── BetterBack/
│   │   ├── BossCommandExecutor/
│   │   ├── CommandTeleport/
│   │   ├── CustomDeathMessages/
│   │   ├── GroupBlacklist/
│   │   ├── ItemHeldMessage/
│   │   ├── ItemPool/
│   │   └── MidiPlayer/
│   └── Shared/           # 公共库
└── manifest.json         # 每个插件标注 category 字段
~~~

**manifest.json 建议字段**：
~~~
{
  "name": "EdgeStitch",
  "category": "sync",
  "categories": ["sync", "extension"],
  "summary": "多世界边缘无缝换乘 + 换乘时世界状态同步"
}
~~~

## 四、注意事项

1. **一个插件可以跨类**（EdgeStitch 既是同步类也是拓展类）→ 用 categories 数组，主分类放 category。
2. **归类会影响加载优先级**：修复类应最早加载（InitializationOrder 小）；同步类要早于拓展/功能类（否则拓展类可能读到未对齐的状态）。
3. 目前 src/ 下是平铺的，改成分层目录需要同步改 .csproj 路径与 CI 脚本 —— **建议在下一个大版本再动**，当前先用本文档做索引。
