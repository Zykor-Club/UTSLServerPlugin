using System.Collections.Generic;

namespace UTSL.VirtualWorld;

/// <summary>单个假世界实例的只读快照（给管理器 / 运维指令显示用）。</summary>
public sealed class VirtualWorldInstanceInfo
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Group { get; init; } = "";
    public string OwnerKey { get; init; } = "";
    public int TilesX { get; init; }
    public int TilesY { get; init; }
    public int Occupants { get; init; }
    public bool Loaded { get; init; }
    public long WorldFileBytes { get; init; }
    public double IdleSeconds { get; init; }
    public string TemplateKey { get; init; } = "";
}

/// <summary>核心实例总览。</summary>
public sealed class VirtualWorldStats
{
    public int Total { get; init; }
    public int Loaded { get; init; }
    public int Occupants { get; init; }
    public IReadOnlyList<VirtualWorldInstanceInfo> Instances { get; init; } = [];
}

/// <summary>
/// 可选能力：核心额外实现本接口，管理器可以 core as IVirtualWorldStatsProvider 取快照。
/// 故意不并进 IVirtualWorldCore：已编译的玩法无需重新编译，契约版本也不必 bump。
/// </summary>
public interface IVirtualWorldStatsProvider
{
    VirtualWorldStats GetStats();
}
