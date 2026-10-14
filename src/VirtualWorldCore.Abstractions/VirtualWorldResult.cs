namespace UTSL.VirtualWorld;

/// <summary>核心操作失败的原因（机器可读；玩法可据此决定提示文案）。</summary>
public enum VirtualWorldFailure
{
    None = 0,
    NotLoaded,          // 实例不存在 / 未加载
    NotRunning,         // 世界还没跑起来 / 调度器不可用
    SameWorld,          // 源与目标相同
    PolicyRejected,     // 玩法策略否决（CanJoin / CanDestroy）
    EventCanceled,      // 被事件监听方否决（Transferring / Destroying）
    TransferRejected,   // 转移被静默拒绝（生效复核不通过）
    Occupied,           // 世界里仍有 active 玩家
    OnReturnPath,       // 仍挂在某玩家的返回路径上（不可回收）
    Exception,          // 内部异常
}

/// <summary>
/// 核心操作结果：成功与否 + 原因码 + 人类可读说明。
/// 玩法拿到后可以直接决定给玩家发什么提示，不必再去翻日志。
/// </summary>
public readonly struct VirtualWorldResult
{
    public bool Success { get; }

    public VirtualWorldFailure Reason { get; }

    public string Message { get; }

    private VirtualWorldResult(bool success, VirtualWorldFailure reason, string message)
    {
        Success = success;
        Reason = reason;
        Message = message;
    }

    public static readonly VirtualWorldResult Ok = new(true, VirtualWorldFailure.None, "");

    public static VirtualWorldResult Fail(VirtualWorldFailure reason, string message) => new(false, reason, message);

    public override string ToString() => Success ? "OK" : $"{Reason}: {Message}";
}
