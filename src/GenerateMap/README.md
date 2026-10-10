# GenerateMap 多世界地图渲染
- **作者**: 少司命, Cai, 千亦（UTSL 移植 + 多世界拼接：星梦）
- **版本**: v2.2.0
## 功能概述
- 把世界渲染成 PNG 地图图片
- **多世界拼接**：按世界链顺序把所有世界横向拼成一张大图，用于验收接缝
- 接缝处可选画红色标记线，方便定位

## 指令
| 语法 | 权限 | 说明 |
|:--|:--|:--|
| `/map img` | `generatemap` | 渲染当前世界为 PNG |
| `/map all` | `generatemap` | 渲染所有运行中的世界 |
| `/map 拼接`（或 `/map stitch`） | `generatemap` | 所有世界横向拼接成一张大图 |

输出目录：`GenerateMap/Images/`

## 移植说明
- `Main.*` → `server.Main.*`（UTSL 的每世界状态都在 ServerContext 上）
- **渲染必须在世界线程上执行**：原版用 `Task.Run` 丢后台线程，但 UTSL 下读图格属于访问该世界状态，
  跨线程不安全 —— 本移植统一用 `server.Dispatcher.Invoke(...)` 包住渲染
- 去掉了 `.map` 文件功能（依赖客户端玩家路径），只保留图片渲染
- `SixLabors.ImageSharp` 走 `ModuleDependencies` + `NugetDependency` 运行时拉取，不打进包
