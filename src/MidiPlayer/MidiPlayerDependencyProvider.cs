using System;
using System.Collections.Generic;
using UnifierTSL.Module;
using UnifierTSL.Module.Dependencies;

namespace MidiPlayer;

/// <summary>
/// MidiPlayer 的私有依赖声明。
/// UTSL 不会把第三方 DLL 打进 Plugins.zip，而是在运行时按这里的声明去拉取 NuGet 包
/// （与 UTSL 自带的 Atelier 插件用同一套机制）。
/// </summary>
public class MidiPlayerDependencyProvider : IDependencyProvider
{
    public IReadOnlyList<ModuleDependency> GetDependencies()
    {
        var asm = typeof(MidiPlayerDependencyProvider).Assembly;
        return
        [
            new NugetDependency(asm, "Melanchall.DryWetMidi", new Version(8, 0, 3)),
        ];
    }
}
