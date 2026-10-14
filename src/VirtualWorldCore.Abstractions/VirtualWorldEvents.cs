using TShockAPI;

namespace UTSL.VirtualWorld;

/// <summary>
/// 玩家在假世界之间转移（进入/离开）的事件参数。**可否决**：
/// 任何一个监听方把 <see cref="Cancel"/> 置 true，核心就放弃本次转移。
/// 用途：反作弊、日志审计、活动规则、临时禁入等"中立插件"不必成为玩法也能插手。
/// </summary>
public sealed class VirtualWorldTransferArgs
{
    public required TSPlayer Player { get; init; }

    public required IVirtualWorld World { get; init; }

    /// <summary>true = 进入该世界；false = 离开该世界。</summary>
    public required bool IsEnter { get; init; }

    /// <summary>置 true 否决本次转移。</summary>
    public bool Cancel { get; set; }

    /// <summary>否决原因（会写进日志与返回结果）。</summary>
    public string? CancelReason { get; set; }
}

/// <summary>销毁实例前的事件参数。**可否决**。</summary>
public sealed class VirtualWorldDestroyArgs
{
    public required IVirtualWorld World { get; init; }

    /// <summary>置 true 否决本次销毁（核心会重置闲置计时，稍后再试）。</summary>
    public bool Cancel { get; set; }

    public string? CancelReason { get; set; }
}
