using System;
using System.Collections.Generic;
using UnifierTSL.Module;
using UnifierTSL.Module.Dependencies;

namespace GenerateMap;

/// <summary>
/// GenerateMap 的私有依赖声明。
/// UTSL 不把第三方 DLL 打进 Plugins.zip，而是运行时按这里的声明去拉 NuGet 包
/// （与 MidiPlayer 拉 DryWetMidi、UTSL 自带 Atelier 是同一套机制）。
/// </summary>
public class GenerateMapDependencyProvider : IDependencyProvider
{
    public IReadOnlyList<ModuleDependency> GetDependencies()
    {
        var asm = typeof(GenerateMapDependencyProvider).Assembly;
        return
        [
            new NugetDependency(asm, "SixLabors.ImageSharp", new Version(3, 1, 5)),
        ];
    }
}
