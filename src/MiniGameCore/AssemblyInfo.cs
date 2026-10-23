using UnifierTSL.Module;

// 小游戏核心是【模块本体】：模块名 = 程序集名 MiniGameCore。
// 同目录下带 [RequiresCoreModule("MiniGameCore")] 的玩法（如 Parkour）会被加载进同一个 ALC，
// 契约程序集（MiniGameCore.Abstractions）随包部署到 plugins/MiniGameCore/lib/。
[assembly: CoreModule]