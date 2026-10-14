namespace UTSL.VirtualWorld;

/// <summary>
/// 稳定的服务发现接口：玩法插件只需认这个接口，**不再依赖核心插件的具体类型名**。
/// 由 VirtualWorldCore 插件实现；玩法在 BeforeGlobalInitialize 里
/// <c>c.Plugin is IVirtualWorldCoreProvider</c> 取用。
/// </summary>
public interface IVirtualWorldCoreProvider
{
    /// <summary>核心服务实例。</summary>
    IVirtualWorldCore Api { get; }

    /// <summary>本实现遵循的契约版本，调用方应校验 <see cref="VirtualWorldApi.Version"/>。</summary>
    int ApiVersion { get; }
}
