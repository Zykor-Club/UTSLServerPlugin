using UTSL.VirtualWorld;
namespace UTSL.MiniGame;

/// <summary>
/// 一个玩法（子插件）。核心只调这些钩子，不解释玩法内部。
/// 钩子都在【房间世界所属的线程】上调用（核心负责切线程），实现里不要做长阻塞。
/// </summary>
public interface IMiniGame
{
    /// <summary>玩法 id（如 "parkour"）。用于排行榜 game_id 与房间 key 前缀。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>本玩法支持哪些地图（P1 允许硬编码，P4 改为读 JSON）。</summary>
    IReadOnlyList<IMapDefinition> Maps { get; }

    IMapDefinition? FindMap(string mapId);

    bool Supports(MiniGameMode mode);

    /// <summary>
    /// 建房前配置世界请求：模板基线名、种子世界、清空策略等（玩法决定，核心只管建）。
    /// </summary>
    void ConfigureWorld(IMapDefinition map, VirtualWorldRequest request, TShockAPI.TSPlayer player);

    /// <summary>房间世界加载完、玩家进来之前（在房间世界线程上）：摆起点平台、清场、放标记。</summary>
    void OnBeforeEnter(IMiniGameRoom room, UnifierTSL.Servers.ServerContext context);

    /// <summary>房间进入 Running 之前调用一次：把玩家放到起点、清状态、给初始装备。</summary>
    void OnRoundStart(IMiniGameRoom room);

    /// <summary>倒计时结束、正式开跑（核心切 Running 时调用）。</summary>
    void OnRaceStarted(IMiniGameRoom room);

    /// <summary>玩家已经进入房间世界（核心完成传送后调用）。</summary>
    void OnPlayerJoined(IMiniGameRoom room, int playerIndex);

    /// <summary>玩家离开房间世界（离开原因见 <paramref name="reason"/>）。</summary>
    void OnPlayerLeft(IMiniGameRoom room, int playerIndex, MiniGameLeaveReason reason);

    /// <summary>
    /// 每 tick 调用（只在 Running / Settling 阶段）。玩法在这里做区域判定、越界检查、计时判断。
    /// </summary>
    void Tick(IMiniGameRoom room, long tick);

    /// <summary>
    /// 结算时调用：玩法算出名次与分数（核心只负责落库）。
    /// 返回空数组 = 本局不计入排行榜（例如全员 DNF）。
    /// </summary>
    IReadOnlyList<ScoreEntry> BuildResults(IMiniGameRoom room);
}
