using UnifierTSL.Module;

// 标记为核心模块：UTSL 会把本插件归到 plugins/VirtualWorldCore/，
// 并把 [RequiresCoreModule("VirtualWorldCore")] 的玩法加载进【同一个 ALC】，
// 从而保证 VirtualWorldCore.Abstractions 的类型身份全服唯一（见 ModuleAssemblyLoader.cs:249）。
[assembly: CoreModule]
