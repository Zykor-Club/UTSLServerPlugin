using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TShockAPI;

namespace UTSL.MiniGame;

/// <summary>
/// 小游戏核心服务：房间生命周期 + 大厅/等候厅 + 排行榜存储。
/// 玩法在 BeforeGlobalInitialize 里按契约接口发现本服务并 <see cref="RegisterGame"/>。
/// </summary>
public interface IMiniGameService
{
    /// <summary>当前登记的玩法（快照）。</summary>
    IReadOnlyList<IMiniGame> Games { get; }

    /// <summary>当前所有房间（快照）。</summary>
    IReadOnlyList<IMiniGameRoom> Rooms { get; }

    IMiniGameRoom? FindRoom(string roomId);

    /// <summary>玩家当前所在房间；不在任何房间返回 null。</summary>
    IMiniGameRoom? FindByPlayer(int playerIndex);

    /// <summary>玩法注册（重复注册同 id 会替换并打日志）。</summary>
    void RegisterGame(IMiniGame game);

    // ===== 进入 / 离开 =====

    /// <summary>单人：直接进房间（没房就建），进入即开始计时。</summary>
    Task<MiniGameJoinResult> JoinSoloAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct);

    /// <summary>多人：进等候厅排队；满员/全员就绪/超时后整批进房间。</summary>
    Task<MiniGameJoinResult> JoinQueueAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct);


    /// <summary>
    /// 建造/编辑模式：进一个**可编辑**的同尺寸世界（不自动开局、不计时），给作者圈区域用。
    /// 房间状态停在 Preparing，玩家可以随便改世界；用 /pk 相关命令把区域记下来。
    /// </summary>
    Task<MiniGameJoinResult> JoinBuildAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct);

    /// <summary>
    /// 预热某个玩法/地图的世界（建好、加载好，但**不传送玩家**）。给 /pk prep 这类命令用。
    /// </summary>
    Task<bool> PrepareWorldAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct);

    /// <summary>离开当前房间/等候厅，回大厅。</summary>
    Task LeaveAsync(TSPlayer player, CancellationToken ct);

    // ===== 排行榜（机制在核心，语义在玩法）=====

    ILeaderboardStore Leaderboard { get; }

    // ===== 事件（供观察者/其它插件挂）=====

    event System.Action<IMiniGameRoom>? RoomStarted;
    event System.Action<IMiniGameRoom>? RoomSettled;
    event System.Action<TSPlayer, IMiniGameRoom>? PlayerEntered;
    event System.Action<TSPlayer, IMiniGameRoom, MiniGameLeaveReason>? PlayerLeft;
}

/// <summary>服务发现入口：玩法只需认这个接口，不绑定核心插件的类型名。</summary>
public interface IMiniGameServiceProvider
{
    IMiniGameService Api { get; }

    /// <summary>本实现遵循的契约版本，调用方应校验 <see cref="MiniGameApi.Version"/>。</summary>
    int ApiVersion { get; }
}

/// <summary>
/// 排行榜存储（机制）：核心建表、写入、查询、缓存、清榜。**排序键与方向由调用方给**，
/// 核心不认识 "ticks 越小越好" 这类语义（那是玩法的事）。
/// </summary>
public interface ILeaderboardStore
{
    Task SubmitAsync(IReadOnlyList<ScoreEntry> entries, CancellationToken ct);

    /// <param name="ascending">true = 越小越好（如跑酷用时）；false = 越大越好（如分数）。</param>
    Task<IReadOnlyList<ScoreEntry>> TopAsync(string gameId, string mapId, string? mode, string? season,
                                             int take, bool ascending, CancellationToken ct);

    Task<IReadOnlyList<ScoreEntry>> BestOfAsync(string gameId, string mapId, string playerKey, string? season, CancellationToken ct);
}
