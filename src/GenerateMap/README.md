# GenerateMap 多世界地图渲染
- **作者**: 少司命; Cai; 千亦; 星梦（UTSL 移植 + 多世界拼接）
- **版本**: v2.2.0

## 功能概述
- 把世界渲染成 PNG 地图图片
- 多世界拼接：按世界链顺序把所有世界横向拼成一张大图，用于验收接缝
- 接缝处可选画红色标记线，方便定位
- 提供接缝剖面分析（各世界边缘的地表高度与方块分布）
- 提供抹海缝合工具的分析命令（各边缘海洋范围、宝箱位置、内陆材质）

## 指令
| 语法 | 权限 | 说明 |
|------|:----:|------|
| /map | generatemap | 显示帮助 |
| /map img | generatemap | 渲染玩家当前所在世界为 PNG |
| /map all | generatemap | 渲染所有运行中的世界 |
| /map 拼接（/map stitch） | generatemap | 所有世界按世界链顺序横向拼接成一张大图 |
| /map 剖面 | generatemap | 输出各世界边缘的地表高度与方块分布（写入服务器日志） |
| /stitch | generatemap | 显示抹海缝合工具帮助 |
| /stitch 分析 | generatemap | 量出各边缘的海洋范围、宝箱位置、内陆材质（写入服务器日志） |

> /map 的别名：生成地图、generatemap；/stitch 的别名：缝合。

输出目录：`GenerateMap/Images/`

## 配置
配置文件路径：无（本插件没有 JSON 配置；输出目录固定在服务器工作目录下的 `GenerateMap/Images/`，首次加载时自动创建）

```json
{}
```

## 配置说明
| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|:------:|------|
| （无） | - | - | 本插件没有配置项 |

## 说明
- Main.* -> server.Main.*（UTSL 的每世界状态都在 ServerContext 上）
- **渲染必须在世界线程上执行**：原版用 Task.Run 丢后台线程，但 UTSL 下读图格属于访问该世界状态，跨线程不安全 —— 本移植统一用 server.Dispatcher 包住渲染
- 渲染按世界链顺序排序，否则拼接图的世界顺序会乱（协调器内部顺序不保证与世界链一致）
- 拼接时默认按 EdgeStitch 的「切片间距_格」传 Δ，否则拼接线会错位
- 去掉了 .map 文件功能（依赖客户端玩家路径），只保留图片渲染
- SixLabors.ImageSharp 走 ModuleDependencies + NugetDependency 运行时拉取，不打进包

## 插件版本
### v2.2.0
- UTSL 移植 + 多世界拼接的当前版本；原 README 未提供更新日志
- 支持按世界链横向拼接、接缝剖面分析、抹海缝合分析

## 反馈
- 优先发 issue -> https://github.com/Zykor-Club/UTSLServerPlugin
- 次优先：TShock官方群：816771079
