namespace UTSL.MiniGame;

/// <summary>玩法模式。单人=不用等；多人=先进等候厅凑人。</summary>
public enum MiniGameMode
{
    Solo = 0,
    Multi = 1,
}

/// <summary>房间状态机（见 94 号文档 §2）。</summary>
public enum MiniGameRoomState
{
    /// <summary>空房，可复用（世界可能仍加载着）。</summary>
    Idle = 0,

    /// <summary>凑人/倒计时阶段（多人等候厅；单人也短暂经过）。</summary>
    Preparing = 1,

    /// <summary>计时中。</summary>
    Running = 2,

    /// <summary>首名完赛后宽限期（仍可完赛记名次）。</summary>
    Settling = 3,

    /// <summary>结算完，正在重置到基线，随后回 Idle。</summary>
    Resetting = 4,
}

/// <summary>玩家离开房间的原因（玩法据此决定是否算 DNF、是否发提示）。</summary>
public enum MiniGameLeaveReason
{
    /// <summary>自己退出（回大厅）。</summary>
    Quit = 0,

    /// <summary>掉线。</summary>
    Disconnect = 1,

    /// <summary>被核心/管理员强制移出。</summary>
    Kicked = 2,

    /// <summary>房间结算/重置。</summary>
    RoomReset = 3,

    /// <summary>服务端关闭。</summary>
    ServerShutdown = 4,
}

/// <summary>加入请求的结果（失败原因直接给玩家看）。</summary>
public readonly struct MiniGameJoinResult
{
    public bool Ok { get; }

    /// <summary>失败原因；Ok=true 时为 null。</summary>
    public string? Reason { get; }

    /// <summary>成功时所在的房间 id。</summary>
    public string? RoomId { get; }

    private MiniGameJoinResult(bool ok, string? reason, string? roomId)
    {
        Ok = ok;
        Reason = reason;
        RoomId = roomId;
    }

    public static MiniGameJoinResult Success(string roomId) => new(true, null, roomId);

    public static MiniGameJoinResult Fail(string reason) => new(false, reason, null);
}

/// <summary>
/// 一条成绩（核心只存不算：名次与分数由玩法在结算时算好）。
/// <see cref="Metric"/> 是排序主键，<see cref="Ascending"/> 由查询侧传入 —— 核心不认识"时间"或"分数"。
/// </summary>
public readonly record struct ScoreEntry(
    string GameId,
    string MapId,
    string Mode,
    string Season,
    string PlayerKey,
    string PlayerName,
    long Metric,
    int Score,
    int Rank,
    string? RoomId,
    string? ExtrasJson);
