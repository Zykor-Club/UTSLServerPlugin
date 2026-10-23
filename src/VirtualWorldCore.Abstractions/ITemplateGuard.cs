namespace UTSL.VirtualWorld;

/// <summary>
/// 玩法的模板约束（v8 新增）。核心只把它当回调，不看玩法语义：
/// 尺寸该不该限、限多少，由玩法自己决定；核心只保留一条"资源硬上限"防止把内存/磁盘打爆。
///
/// 注册方式：<c>core.RegisterTemplateGuard("我的玩法", guard)</c>，group 传空或 "*" 表示对所有玩法生效。
/// 调用时机：<see cref="IVirtualWorldCore.SaveTemplateAsync"/>、
/// <see cref="IVirtualWorldCore.SaveRegionAsync"/>、<see cref="IVirtualWorldCore.ApplyTemplateAsync"/> 之前。
/// </summary>
public interface ITemplateGuard
{
    /// <summary>
    /// 校验一个模板。返回 null = 放行；返回非空字符串 = 拒绝原因（会原样回给调用方并在日志里记一行）。
    /// </summary>
    /// <param name="info">模板名与尺寸（整世界模板 = 世界尺寸；区块模板 = .utpl 的尺寸）。</param>
    /// <param name="bytes">模板文件字节数；尚未落盘或未知时为 -1。</param>
    /// <param name="group">发起请求的实例玩法组（<see cref="IVirtualWorld.Group"/>）。</param>
    string? ValidateTemplate(VirtualWorldTemplateInfo info, long bytes, string group);
}
