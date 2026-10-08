using MidiPlayer;
using UnifierTSL.Module;

// UTSL：声明本插件的私有依赖，宿主会在加载插件前先解析它们。
// 对应 UTSL 的 ModuleAssemblyLoader / DependenciesConfiguration（生成 dependencies.json）。
[assembly: ModuleDependencies<MidiPlayerDependencyProvider>]
