using UTSL.VirtualWorld;

namespace UTSL.MiniGame;

/// <summary>
/// 一个游戏房间。核心负责状态机、传送、计时、结算落库；玩法负责判定与名次（通过 <see cref="RequestFinish"/> 通知）。
/// 房间背后是一个假世界实例（<see cref="World"/>）。
/// </summary>
public interface IMiniGameRoom
{
    string RoomId { get; }

    string GameId { get; }

    IMapDefinition Map { get; }

    MiniGameMode Mode { get; }

    MiniGameRoomState State { get; }

    /// <summary>背后已加载的假世界；房间未加载时为 null。</summary>
    IVirtualWorld? World { get; }

    /// <summary>当前在房间里的玩家索引（只读快照）。</summary>
    IReadOnlyCollection<int> Players { get; }

    /// <summary>房间开始计时后累计的 tick 数（1 tick ≈ 1/60 秒，按服务器帧推进）。</summary>
    long TickCount { get; }

    /// <summary>进入 Running 的时刻；未开始时为 default。</summary>
    DateTime StartedUtc { get; }

    /// <summary>玩法自定义的每房间状态（核心不解释其内容）。</summary>
    T GetState<T>() where T : class, new();

    /// <summary>玩法上报玩家进度（存档点 index）。核心只存，用于未完赛者的排名参考。</summary>
    void SetProgress(int playerIndex, int progress);

    /// <summary>玩法判定"该玩家到终点了"。核心负责：记录名次、通知进入宽限、首次触发结算倒计时。</summary>
    void RequestFinish(int playerIndex);

    /// <summary>玩法强制结束本局（例如全员掉线、地图异常）。</summary>
    void RequestEnd(string reason);

    /// <summary>倒计时剩余 tick（0 = 不在倒计时）。</summary>
    long CountdownTicksLeft { get; }

    /// <summary>
    /// 开始倒计时（秒）。倒计时期间房间处于 <see cref="MiniGameRoomState.Preparing"/>，
    /// 玩法在 Tick 里负责"把人按在起点区域内"；到 0 时核心切 Running 并回调 <see cref="IMiniGame.OnRaceStarted"/>。
    /// </summary>
    void BeginCountdown(int seconds);
}
