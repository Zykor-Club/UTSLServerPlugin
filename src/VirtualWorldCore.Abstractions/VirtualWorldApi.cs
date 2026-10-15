namespace UTSL.VirtualWorld;

/// <summary>契约版本。接口只增不改；破坏性变更必须提升此值，玩法发现时校验。</summary>
public static class VirtualWorldApi
{
    public const int Version = 6;   // v6: ListTemplates / SaveTemplateAsync（建筑模板导出与列举）
}
