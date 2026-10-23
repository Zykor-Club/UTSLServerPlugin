using UTSL.VirtualWorld;

namespace UTSL.MiniGame;

/// <summary>
/// 一个游戏房间（P1：单人可跑通；多人在 P2 接等候厅）。
/// 背后是一个 VirtualWorldCore 的假世界实例，房间只负责：状态机、玩家集合、计时、结算触发。
/// </summary>
internal sealed class MiniGameRoom : IMiniGameRoom
{
    private readonly object gate = new();
    private readonly Dictionary<int, int> progress = new();     // playerIndex → 存档点 index
    private readonly Dictionary<int, long> finishTicks = new(); // playerIndex → 完赛 tick（第一次）
    private readonly List<int> players = new();
    private readonly Dictionary<Type, object> states = new();

    public string RoomId { get; }
    public string GameId { get; }
    public IMapDefinition Map { get; }
    public MiniGameMode Mode { get; }
    public MiniGameRoomState State { get; internal set; } = MiniGameRoomState.Idle;
    public IVirtualWorld? World { get; internal set; }
    public long TickCount { get; internal set; }
    public DateTime StartedUtc { get; internal set; }
    public string Tag { get; }

    /// <summary>世界文件/服务名（净化过，不能带冒号）。</summary>
    internal string WorldFileBase { get; init; } = "MG_room";

    /// <summary>首名完赛的 tick；-1 = 还没人完赛。</summary>
    internal long FirstFinishTick { get; private set; } = -1;

    /// <summary>倒计时剩余 tick（0 = 不在倒计时）。</summary>
    public long CountdownTicksLeft { get; internal set; }

    /// <summary>结算幂等标记（0 = 未结算，1 = 已结算）。Interlocked 使用。</summary>
    internal int SettledFlag;

    /// <summary>回 Idle 的时刻（用于空闲回收）。</summary>
    internal DateTime IdleSinceUtc { get; set; }

    public void BeginCountdown(int seconds)
    {
        long ticks = seconds > 0 ? seconds * 60L : 0;
        lock (gate)
        {
            CountdownTicksLeft = ticks;
            State = ticks > 0 ? MiniGameRoomState.Preparing : MiniGameRoomState.Running;
        }
    }

    /// <summary>结算被请求的原因（诊断用）。</summary>
    internal string? EndReason { get; private set; }

    internal MiniGameRoom(string roomId, string gameId, IMapDefinition map, MiniGameMode mode)
    {
        RoomId = roomId;
        GameId = gameId;
        Map = map;
        Mode = mode;
        Tag = $"[房间 {roomId}]";
    }

    public IReadOnlyCollection<int> Players
    {
        get { lock (gate) { return players.ToArray(); } }
    }

    public T GetState<T>() where T : class, new()
    {
        lock (states)
        {
            if (states.TryGetValue(typeof(T), out object? v) && v is T t) { return t; }
            T created = new();
            states[typeof(T)] = created;
            return created;
        }
    }

    public void SetProgress(int playerIndex, int value)
    {
        lock (gate)
        {
            int old = progress.TryGetValue(playerIndex, out int p) ? p : -1;
            if (value > old) { progress[playerIndex] = value; }
        }
    }


    public void RequestFinish(int playerIndex)
    {
        lock (gate)
        {
            if (finishTicks.ContainsKey(playerIndex)) { return; }   // 幂等：只记第一次
            finishTicks[playerIndex] = TickCount;
            if (FirstFinishTick < 0) { FirstFinishTick = TickCount; }
        }
    }

    public void RequestEnd(string reason)
    {
        lock (gate) { EndReason ??= reason; }
    }



    internal int FinishedCount
    {
        get { lock (gate) { return finishTicks.Count; } }
    }

    internal void AddPlayer(int playerIndex)
    {
        lock (gate) { if (!players.Contains(playerIndex)) { players.Add(playerIndex); } }
    }

    internal void RemovePlayer(int playerIndex)
    {
        lock (gate) { players.Remove(playerIndex); }
    }

    internal void ResetRound()
    {
        lock (gate)
        {
            players.Clear();
            progress.Clear();
            finishTicks.Clear();
            FirstFinishTick = -1;
            EndReason = null;
            TickCount = 0;
            CountdownTicksLeft = 0;
            SettledFlag = 0;
            StartedUtc = default;
            State = MiniGameRoomState.Idle;
        }
    }
}
