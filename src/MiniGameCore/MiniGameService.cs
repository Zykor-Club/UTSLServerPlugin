using System.Collections.Concurrent;
using System.Text;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Servers;
using UTSL.VirtualWorld;

namespace UTSL.MiniGame;

/// <summary>
/// 小游戏核心服务（P1）：房间生命周期 + 单人开局 + 计时 + 结算。
/// 多人等候厅见 P2；排行榜落库见 P3。
/// </summary>
internal sealed class MiniGameService : IMiniGameService
{
    internal static MiniGameCoreConfig Config { get; set; } = new();

    private readonly ConcurrentDictionary<string, IMiniGame> games = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MiniGameRoom> rooms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> playerRoom = new();   // playerIndex → roomId

    /// <summary>房间创建/复用的互斥锁（连点、多人同时进才不会建出两个房间）。</summary>
    private readonly object roomGate = new();

    private IVirtualWorldCore? core;

    public IReadOnlyList<IMiniGame> Games => games.Values.ToArray();

    public IReadOnlyList<IMiniGameRoom> Rooms => rooms.Values.Cast<IMiniGameRoom>().ToArray();

    public ILeaderboardStore Leaderboard { get; } = new NullLeaderboardStore();

    public event Action<IMiniGameRoom>? RoomStarted;
    public event Action<IMiniGameRoom>? RoomSettled;
    public event Action<TSPlayer, IMiniGameRoom>? PlayerEntered;
    public event Action<TSPlayer, IMiniGameRoom, MiniGameLeaveReason>? PlayerLeft;

    /// <summary>由插件在 BeforeGlobalInitialize 里注入假世界核心。</summary>
    internal void AttachVirtualWorldCore(IVirtualWorldCore api) => core = api;

    public IMiniGameRoom? FindRoom(string roomId) => rooms.TryGetValue(roomId, out MiniGameRoom? r) ? r : null;

    public IMiniGameRoom? FindByPlayer(int playerIndex) => FindByPlayer(playerIndex, out _);

    /// <summary>
    /// 以【路由】为准判断玩家是否还在房间里（铁律②③）：路由对不上就清掉过期映射，
    /// 避免"人已经回主世界了，/pk solo 还说你已经在房间里"。
    /// </summary>
    private IMiniGameRoom? FindByPlayer(int playerIndex, out bool cleaned)
    {
        cleaned = false;
        if (!playerRoom.TryGetValue(playerIndex, out string? id)) { return null; }
        if (!rooms.TryGetValue(id, out MiniGameRoom? r)) { playerRoom.TryRemove(playerIndex, out _); cleaned = true; return null; }

        ServerContext? here = UnifiedServerCoordinator.GetClientCurrentlyServer(playerIndex);
        if (r.World?.Context is ServerContext roomCtx)
        {
            bool inside = here is not null && ReferenceEquals(here, roomCtx);
            if (!inside)
            {
                r.RemovePlayer(playerIndex);
                playerRoom.TryRemove(playerIndex, out _);
                cleaned = true;
                Info($"[自愈] 玩家 {playerIndex} 的路由不在 {r.Tag} 里，已清理过期房间映射");
                return null;
            }
        }
        return r;
    }

    public void RegisterGame(IMiniGame game)
    {
        games[game.Id] = game;
        Info($"[登记] 玩法 {game.Id}（{game.DisplayName}），地图 {game.Maps.Count} 张");
    }

    // ==================== 单人：直接开局 ====================

    public async Task<MiniGameJoinResult> JoinSoloAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct)
    {
        if (core is null) { return MiniGameJoinResult.Fail("假世界核心未就绪（VirtualWorldCore 未加载）"); }
        if (!games.TryGetValue(gameId, out IMiniGame? game)) { return MiniGameJoinResult.Fail($"没有这个玩法: {gameId}"); }
        if (!game.Supports(MiniGameMode.Solo)) { return MiniGameJoinResult.Fail($"{game.DisplayName} 不支持单人模式"); }
        IMapDefinition? map = game.FindMap(mapId);
        if (map is null) { return MiniGameJoinResult.Fail($"没有这张地图: {mapId}"); }
        // 自愈：路由已不在房间世界里的过期映射会在这里被清掉（重进不会再报"你已经在房间里"）
        if (FindByPlayer(player.Index) is not null) { return MiniGameJoinResult.Fail("你已经在某个房间里了（先 /pk leave）"); }

        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(player.Index);
        if (from is null) { return MiniGameJoinResult.Fail("取不到你当前所在的世界"); }
        // 地图尺寸 = 0 表示"跟随玩家所在世界"（跑酷测试图/同尺寸约定用它，避免初始化期依赖主世界）
        int needX = map.TilesX > 0 ? map.TilesX : from.Main.maxTilesX;
        int needY = map.TilesY > 0 ? map.TilesY : from.Main.maxTilesY;
        bool sizeMatch = from.Main.maxTilesX == needX && from.Main.maxTilesY == needY;
        if (!sizeMatch)
        {
            // 跨尺寸：核心不拦（铁律允许），由玩法决定；这里只在日志里留证据
            Warn($"[开局] 跨尺寸：地图 {needX}x{needY}，玩家世界 {from.Main.maxTilesX}x{from.Main.maxTilesY}（{player.Name}）");
        }

        if (!TryAcquireRoom(game, map, MiniGameMode.Solo, map.Capacity, null, null, out MiniGameRoom? acquired, out string? acquireFail) || acquired is null)
        {
            return MiniGameJoinResult.Fail(acquireFail ?? "取不到房间");
        }
        MiniGameRoom room = acquired;

        try
        {
            Info($"[开局] {player.Name} 请求 {game.Id}/{map.Id}（{from.Name} {from.Main.maxTilesX}x{from.Main.maxTilesY}）");
            IVirtualWorld world = await EnsureRoomWorldAsync(room, from, player, ct).ConfigureAwait(false);
            // ★ 1.4.5.8 的硬限制：无缝转移要求"源世界与目标世界尺寸完全一致"。
            //   这里在建好世界后先比对，给出人能看懂的提示，而不是让 TransferRejected 冒出来。
            if (world.Context is ServerContext roomWorldCtx)
            {
                int rw = roomWorldCtx.Main.maxTilesX, rh = roomWorldCtx.Main.maxTilesY;
                if (rw != from.Main.maxTilesX || rh != from.Main.maxTilesY)
                {
                    Warn($"[开局] 尺寸不匹配：地图世界 {rw}x{rh}，玩家所在 {from.Main.maxTilesX}x{from.Main.maxTilesY}（{room.Tag}）");
                    ReleaseRoom(room);
                    return MiniGameJoinResult.Fail(
                        $"这张图的世界是 {rw}x{rh}，与你当前世界 {from.Main.maxTilesX}x{from.Main.maxTilesY} 不一致。" +
                        "1.4.5.8 的无缝转移要求两边尺寸完全相同，所以进不去。" +
                        "（解法：把主世界换成同尺寸，或在独立的小游戏服里用同尺寸主世界）");
                }
            }

            Info($"[开局] {room.Tag} 世界就绪，开始传送 {player.Name}");
            VirtualWorldResult enter = await core.EnterAsync(player, world, ct).ConfigureAwait(false);
            Info($"[开局] 传送结果 ok={enter.Success} reason={enter.Reason}");
            if (!enter.Success) { return MiniGameJoinResult.Fail("传送失败: " + enter.Reason); }

            room.AddPlayer(player.Index);
            playerRoom[player.Index] = room.RoomId;
            game.OnPlayerJoined(room, player.Index);

            room.TickCount = 0;
            game.OnRoundStart(room);                     // 先按在起点
            int cd = CountdownOf(map);
            room.BeginCountdown(cd);                      // 倒计时（0 = 立刻开跑）
            if (cd <= 0) { StartRace(room, game); }
            else { Info($"{room.Tag} 倒计时 {cd}s（map={map.Id}）"); }

            PlayerEntered?.Invoke(player, room);
            return MiniGameJoinResult.Success(room.RoomId);
        }
        catch (Exception ex)
        {
            Error($"{room.Tag} 单人开局失败: {ex}");
            ReleaseRoom(room);
            return MiniGameJoinResult.Fail("开局失败: " + ex.Message);
        }
    }

    public Task<MiniGameJoinResult> JoinQueueAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct)
        => Task.FromResult(MiniGameJoinResult.Fail("多人等候厅还没做（P2）"));

    /// <summary>预热世界（建好加载好，但不传送玩家）。</summary>
    public async Task<bool> PrepareWorldAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct)
    {
        if (core is null) { return false; }
        if (!games.TryGetValue(gameId, out IMiniGame? game)) { return false; }
        IMapDefinition? map = game.FindMap(mapId);
        if (map is null) { return false; }

        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(player.Index);
        if (from is null) { return false; }

        try
        {
            if (!TryAcquireRoom(game, map, MiniGameMode.Solo, map.Capacity,
                                $"build:{gameId}:{mapId}", $"PK_B_{gameId}_{mapId}",
                                out MiniGameRoom? room, out _) || room is null)
            {
                return false;
            }
            await EnsureRoomWorldAsync(room, from, player, ct).ConfigureAwait(false);
            ReleaseRoom(room);                      // 预热完放回 Idle（没玩家）
            Info($"{room.Tag} 世界已预热（未传送玩家）");
            return true;
        }
        catch (Exception ex)
        {
            Error($"[预热] 失败: {ex}");
            return false;
        }
    }

    /// <summary>建造/编辑模式：进一个可编辑的同尺寸世界（不计时、不开局），给作者圈区域。</summary>
    public async Task<MiniGameJoinResult> JoinBuildAsync(TSPlayer player, string gameId, string mapId, CancellationToken ct)
    {
        if (core is null) { return MiniGameJoinResult.Fail("假世界核心未就绪"); }
        if (!games.TryGetValue(gameId, out IMiniGame? game)) { return MiniGameJoinResult.Fail($"没有这个玩法: {gameId}"); }
        IMapDefinition? map = game.FindMap(mapId);
        if (map is null) { return MiniGameJoinResult.Fail($"没有这张地图: {mapId}"); }

        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(player.Index);
        if (from is null) { return MiniGameJoinResult.Fail("取不到你当前所在的世界"); }

        try
        {
            if (FindByPlayer(player.Index) is not null) { return MiniGameJoinResult.Fail("你已经在某个房间里了（先 /pk leave）"); }

            string buildKey = $"build:{gameId}:{mapId}";
            if (!TryAcquireRoom(game, map, MiniGameMode.Solo, map.Capacity, buildKey, $"PK_B_{gameId}_{mapId}",
                                out MiniGameRoom? acquired, out string? acquireFail) || acquired is null)
            {
                return MiniGameJoinResult.Fail(acquireFail ?? "取不到建造房间");
            }
            MiniGameRoom room = acquired;

            IVirtualWorld world = await EnsureRoomWorldAsync(room, from, player, ct).ConfigureAwait(false);
            if (world.Context is ServerContext bctx)
            {
                int bw = bctx.Main.maxTilesX, bh = bctx.Main.maxTilesY;
                if (bw != from.Main.maxTilesX || bh != from.Main.maxTilesY)
                {
                    ReleaseRoom(room);
                    return MiniGameJoinResult.Fail(
                        $"这张图的世界是 {bw}x{bh}，与你当前世界 {from.Main.maxTilesX}x{from.Main.maxTilesY} 不一致，" +
                        "1.4.5.8 无法无缝转移（同尺寸限制）。");
                }
            }

            VirtualWorldResult enter = await core.EnterAsync(player, world, ct).ConfigureAwait(false);
            if (!enter.Success)
            {
                ReleaseRoom(room);
                return MiniGameJoinResult.Fail("传送失败: " + enter.Reason);
            }

            room.AddPlayer(player.Index);
            playerRoom[player.Index] = room.RoomId;
            game.OnPlayerJoined(room, player.Index);
            Info($"{room.Tag} 建造模式（{map.Id}），玩家 {player.Name}");
            return MiniGameJoinResult.Success(room.RoomId);
        }
        catch (Exception ex)
        {
            Error($"[建造] 进入失败: {ex}");
            return MiniGameJoinResult.Fail("进入建造世界失败: " + ex.Message);
        }
    }


    // ==================== 离开 ====================

    public async Task LeaveAsync(TSPlayer player, CancellationToken ct)
    {
        if (core is null) { return; }
        IMiniGameRoom? room = FindByPlayer(player.Index);
        if (room is null) { return; }

        MiniGameRoom r = (MiniGameRoom)room;
        IMiniGame? game = games.TryGetValue(r.GameId, out IMiniGame? g) ? g : null;

        // ★ 先传送成功再删映射：传送被拒时如果先删了映射，玩家会永久卡在假世界里
        VirtualWorldResult res;
        try { res = await core.LeaveAsync(player, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Warn($"{r.Tag} 离开时回传异常（保留房间映射）: {ex.Message}");
            return;
        }

        if (!res.Success)
        {
            Warn($"{r.Tag} 离开被拒（保留房间映射）: {res.Reason}");
            try { player.SendErrorMessage("[跑酷] 离开失败: " + res.Reason); } catch { }
            return;
        }

        r.RemovePlayer(player.Index);
        playerRoom.TryRemove(player.Index, out _);
        try { game?.OnPlayerLeft(r, player.Index, MiniGameLeaveReason.Quit); } catch (Exception ex) { Warn($"{r.Tag} OnPlayerLeft 异常: {ex.Message}"); }
        PlayerLeft?.Invoke(player, r, MiniGameLeaveReason.Quit);

        if (r.Players.Count == 0 && r.State != MiniGameRoomState.Idle)
        {
            Settle(r, "房间空了", game);
        }
    }

    /// <summary>玩家掉线/被踢：从房间移除并通知假世界核心清记录；房间空了就结算。</summary>
    internal void OnPlayerGone(int playerIndex)
    {
        if (playerRoom.TryRemove(playerIndex, out string? id) && rooms.TryGetValue(id, out MiniGameRoom? r))
        {
            r.RemovePlayer(playerIndex);
            if (games.TryGetValue(r.GameId, out IMiniGame? g))
            {
                try { g.OnPlayerLeft(r, playerIndex, MiniGameLeaveReason.Disconnect); } catch { }
            }
            if (r.Players.Count == 0 && r.State is not (MiniGameRoomState.Idle or MiniGameRoomState.Resetting))
            {
                Settle(r, "房间空了（掉线）", g);
            }
        }
        try { core?.NotifyPlayerGone(playerIndex); } catch { }
    }

    // ==================== 逐帧驱动（每个假世界自己的更新循环）====================

    internal void OnServerFrame(ServerContext ctx)
    {
        if (rooms.IsEmpty) { return; }

        foreach (MiniGameRoom room in rooms.Values)
        {
            if (room.World?.Context is not ServerContext roomCtx || !ReferenceEquals(roomCtx, ctx)) { continue; }
            if (!games.TryGetValue(room.GameId, out IMiniGame? game)) { continue; }

            // 倒计时阶段：推进倒计时，并让玩法每帧把人按在起点区域里
            if (room.State == MiniGameRoomState.Preparing && room.CountdownTicksLeft > 0)
            {
                long before = room.CountdownTicksLeft;
                room.CountdownTicksLeft = Math.Max(0, before - 1);
                try { game.Tick(room, room.CountdownTicksLeft); }
                catch (Exception ex) { Warn($"{room.Tag} 倒计时 Tick 异常: {ex.Message}"); }

                long secsBefore = (before + 59) / 60, secsAfter = (room.CountdownTicksLeft + 59) / 60;
                if (secsAfter != secsBefore && secsAfter > 0) { BroadcastToRoom(room, $"[开始] {secsAfter}…"); }

                if (room.CountdownTicksLeft == 0)
                {
                    StartRace(room, game);
                    BroadcastToRoom(room, "[开始] 出发！");
                }
                continue;
            }

            if (room.State is MiniGameRoomState.Running or MiniGameRoomState.Settling)
            {
                if (room.Players.Count == 0) { Settle(room, "房间空了（无活人）", game); continue; }
            }

            if (room.State != MiniGameRoomState.Running) { continue; }   // Settling 不再 tick（BuildResults 可能并发）

            room.TickCount++;
            try { game.Tick(room, room.TickCount); }
            catch (Exception ex) { Warn($"{room.Tag} Tick 抛异常（下一帧继续）: {ex.Message}"); }

            if (room.State == MiniGameRoomState.Running) { CheckEndConditions(room, game); }

        }
    }

    /// <summary>倒计时秒数：地图优先，缺省 3；0 = 不要倒计时。</summary>
    private static int CountdownOf(IMapDefinition map)
        => map.CountdownSeconds > 0 ? map.CountdownSeconds : 0;

    private void StartRace(MiniGameRoom room, IMiniGame game)
    {
        room.State = MiniGameRoomState.Running;
        room.StartedUtc = DateTime.UtcNow;
        room.TickCount = 0;
        try { game.OnRaceStarted(room); } catch (Exception ex) { Warn($"{room.Tag} OnRaceStarted 异常: {ex.Message}"); }
        RoomStarted?.Invoke(room);
        Info($"{room.Tag} 正式开跑（map={room.Map.Id}）");
    }

    private static void BroadcastToRoom(MiniGameRoom room, string message)
    {
        foreach (int idx in room.Players)
        {
            try { TShock.Players[idx]?.SendInfoMessage(message); } catch { }
        }
    }

    private void CheckEndConditions(MiniGameRoom room, IMiniGame game)
    {
        if (!string.IsNullOrEmpty(room.EndReason)) { Settle(room, room.EndReason!, game); return; }

        if (room.Map.TimeLimitSeconds > 0 && room.TickCount >= room.Map.TimeLimitSeconds * 60L)
        {
            Settle(room, "到达时限", game);
            return;
        }

        if (room.Mode == MiniGameMode.Solo)
        {
            if (room.FinishedCount > 0) { Settle(room, "完赛", game); }
            return;
        }

        // 多人：全员完赛，或首名完赛后走宽限
        int alive = room.Players.Count;
        if (alive > 0 && room.FinishedCount >= alive) { Settle(room, "全员完赛", game); return; }

        if (room.FirstFinishTick >= 0)
        {
            int grace = room.Map.GraceSeconds > 0 ? room.Map.GraceSeconds : Config.GraceSeconds;
            if (room.TickCount - room.FirstFinishTick >= grace * 60L) { Settle(room, $"首名完赛后宽限 {grace}s 到点", game); }
        }
    }


    // ==================== 结算 ====================

    private void Settle(MiniGameRoom room, string reason, IMiniGame? game)
    {
        if (room.State is MiniGameRoomState.Resetting or MiniGameRoomState.Idle) { return; }

        // ★ 结算必须在该房间世界的线程上跑：BuildResults 会读写玩法状态，和 Tick 并发会炸字典
        if (room.World?.Context is ServerContext settleCtx && !settleCtx.Dispatcher.CheckAccess())
        {
            settleCtx.Dispatcher.Post(() => Settle(room, reason, game));
            return;
        }

        // ★ 幂等：结算只能发生一次。多线程/多路径（完赛、超时、房间空了、缓冲到点）可能同时触发。
        if (System.Threading.Interlocked.Exchange(ref room.SettledFlag, 1) == 1) { return; }

        room.State = MiniGameRoomState.Settling;

        IReadOnlyList<ScoreEntry> results = Array.Empty<ScoreEntry>();
        try { results = game?.BuildResults(room) ?? Array.Empty<ScoreEntry>(); }
        catch (Exception ex) { Error($"{room.Tag} 结算算分失败: {ex}"); }

        Info($"{room.Tag} 结算（{reason}）：{results.Count} 条成绩");
        for (int i = 0; i < results.Count; i++)
        {
            ScoreEntry e = results[i];
            Info($"   {room.Tag} #{e.Rank} {e.PlayerName} metric={e.Metric} score={e.Score} extras={e.ExtrasJson}");
        }
        if (results.Count == 0) { Info($"   {room.Tag} （本局无有效成绩）"); }

        RoomSettled?.Invoke(room);
        _ = ResetAsync(room, game);
    }

    private async Task ResetAsync(MiniGameRoom room, IMiniGame? game)
    {
        try
        {
            room.State = MiniGameRoomState.Resetting;
            int[] leaving = room.Players.ToArray();
            foreach (int idx in leaving)
            {
                try
                {
                    TSPlayer? p = TShock.Players[idx];
                    room.RemovePlayer(idx);
                    playerRoom.TryRemove(idx, out _);
                    try { game?.OnPlayerLeft(room, idx, MiniGameLeaveReason.RoomReset); }
                    catch (Exception ex) { Warn($"{room.Tag} OnPlayerLeft 异常: {ex.Message}"); }

                    // 只回传"确实还在这个房间世界里"的玩家（槽位可能已被别人复用）
                    if (p is { Active: true } && core is not null &&
                        room.World?.Context is ServerContext rc &&
                        ReferenceEquals(UnifiedServerCoordinator.GetClientCurrentlyServer(idx), rc))
                    {
                        try
                        {
                            // ★ 必须带超时：核心的 LeaveAsync 依赖目标世界线程回调，世界停摆时会永不返回
                            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                            await core.LeaveAsync(p, cts.Token).WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                        }
                        catch (Exception ex) { Warn($"{room.Tag} 回传玩家 {idx} 失败/超时: {ex.Message}"); }
                    }
                }
                catch (Exception ex) { Warn($"{room.Tag} 重置玩家 {idx} 异常: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Error($"{room.Tag} 重置异常: {ex}"); }
        finally
        {
            room.ResetRound();
            Info($"{room.Tag} 已回到 Idle（世界保留复用）");
        }
    }

    // ==================== 房间与世界的创建 ====================

    /// <summary>
    /// 在锁内【占房】：复用空闲房或新建，并把 State 立刻置 Preparing（占位），
    /// 这样两个玩家同时进同一张图不会塞进同一个单人房。失败必须 Release。
    /// </summary>
    private bool TryAcquireRoom(IMiniGame game, IMapDefinition map, MiniGameMode mode, int capacity,
                                string? explicitKey, string? worldFileBase, out MiniGameRoom? room, out string? fail)
    {
        room = null;
        fail = null;
        lock (roomGate)
        {
            if (Config.MaxRooms > 0 && rooms.Count >= Config.MaxRooms)
            {
                // 顺手回收一批空闲房再判
                SweepIdleRoomsLocked();
                if (rooms.Count >= Config.MaxRooms) { fail = $"房间数已达上限（{Config.MaxRooms}）"; return false; }
            }

            MiniGameRoom? reuse = rooms.Values.FirstOrDefault(r =>
                r.State == MiniGameRoomState.Idle
                && r.GameId.Equals(game.Id, StringComparison.OrdinalIgnoreCase)
                && r.Map.Id.Equals(map.Id, StringComparison.OrdinalIgnoreCase) && r.Mode == mode
                && r.Players.Count < capacity);

            MiniGameRoom target = reuse ?? CreateRoomLocked(game, map, mode, explicitKey, worldFileBase);
            if (target.State != MiniGameRoomState.Idle) { fail = "房间正忙"; return false; }
            if (target.Players.Count >= capacity) { fail = "房间满了"; return false; }

            target.State = MiniGameRoomState.Preparing;   // ★ 立刻占位（配合 roomGate，占房是原子的）
            room = target;
            return true;
        }
    }

    /// <summary>占房失败/异常时把房间放回 Idle（没有玩家才放）。</summary>
    private void ReleaseRoom(MiniGameRoom room)
    {
        lock (roomGate)
        {
            if (room.Players.Count == 0 && room.State is MiniGameRoomState.Preparing)
            {
                room.State = MiniGameRoomState.Idle;
                room.IdleSinceUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>回收空闲太久的房间（含世界）。</summary>
    private void SweepIdleRoomsLocked()
    {
        int ttl = Config.RoomIdleDestroySeconds;
        if (ttl <= 0) { return; }

        DateTime now = DateTime.UtcNow;
        foreach (MiniGameRoom r in rooms.Values.ToArray())
        {
            if (r.State != MiniGameRoomState.Idle || r.Players.Count > 0) { continue; }
            if (r.IdleSinceUtc == default) { r.IdleSinceUtc = now; continue; }
            if ((now - r.IdleSinceUtc).TotalSeconds < ttl) { continue; }

            rooms.TryRemove(r.RoomId, out _);
            if (core is not null && r.World is not null)
            {
                _ = Task.Run(async () =>
                {
                    try { await core.DestroyAsync(r.World, save: false, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { Warn($"{r.Tag} 回收世界失败: {ex.Message}"); }
                });
            }
            Info($"{r.Tag} 空闲超过 {ttl}s，已回收（含世界）");
        }
    }

    private MiniGameRoom CreateRoomLocked(IMiniGame game, IMapDefinition map, MiniGameMode mode,
                                           string? explicitKey, string? worldFileBase)
    {

        // ★ 别对拼接结果做 Substring/切片：长度一算错就抛 "Index and length..."。
        //   roomId 只用于标识/日志（可以带冒号）；世界文件名必须净化（不能带 ':'）。
        string token = Guid.NewGuid().ToString("N");
        string roomId = explicitKey ?? $"{game.Id}:{map.Id}:{token}";
        if (rooms.TryGetValue(roomId, out MiniGameRoom? existing) && existing.State is MiniGameRoomState.Idle or MiniGameRoomState.Preparing)
        {
            return existing;
        }
        MiniGameRoom created = new(roomId, game.Id, map, mode)
        {
            WorldFileBase = SanitizeFile(worldFileBase ?? $"MG_{game.Id}_{map.Id}_{token[..8]}"),
        };
        rooms[roomId] = created;
        Info($"[房间] 新建 {roomId}（{game.Id}/{map.Id}/{mode}）");
        return created;
    }

    private async Task<IVirtualWorld> EnsureRoomWorldAsync(MiniGameRoom room, ServerContext from, TSPlayer player, CancellationToken ct)
    {
        if (room.World is { IsLoaded: true } loaded) { return loaded; }

        // 尺寸来自地图定义（与主世界一致，已在 JoinSoloAsync 校验过）
        // 地图尺寸 0 = 跟随玩家所在世界（进房前房间玩家集合还是空的，别从房间里取）
        int tilesX = room.Map.TilesX > 0 ? room.Map.TilesX : from.Main.maxTilesX;
        int tilesY = room.Map.TilesY > 0 ? room.Map.TilesY : from.Main.maxTilesY;
        VirtualWorldRequest req = new()
        {
            Key = room.RoomId,
            DisplayName = room.Map.DisplayName,
            Group = "MiniGame",
            ServerName = room.WorldFileBase,
            WorldFileName = room.WorldFileBase,
            TilesX = tilesX,
            TilesY = tilesY,
            TemplateKey = string.IsNullOrWhiteSpace(room.Map.WorldTemplate) ? null : room.Map.WorldTemplate,
            // ★ 核心硬性要求：没有模板基线时必须给"种子世界文件"，否则 EnsureAsync 直接抛
            //   "世界文件不存在且未提供种子"。P1 用玩家当前世界文件当种子（同尺寸、一定存在）。
            SeedWorldPath = string.IsNullOrWhiteSpace(room.Map.WorldTemplate) || room.Map.WorldTemplate!.Length == 0
                ? from.Main.worldPathName
                : null,
            LogTag = room.Tag,
            // ★ 假世界必须 Headless（与 BossArena/尘歌壶一致）：非 headless 会走"可见服务器"路径，
            //   客户端拿不到合法的世界数据 → 立刻断线重连，表现就是"进去就被弹回主世界"
            Headless = true,
            IdleDestroySeconds = Config.RoomIdleDestroySeconds,
        };
        // ★ 玩法决定"世界怎么建"（模板基线/种子/清空）；核心只负责建
        if (games.TryGetValue(room.GameId, out IMiniGame? gm))
        {
            try { gm.ConfigureWorld(room.Map, req, player); }
            catch (Exception ex) { Warn($"{room.Tag} ConfigureWorld 异常: {ex.Message}"); }
            req.OnBeforeEnter = (ctx, _) =>
            {
                try { gm.OnBeforeEnter(room, ctx); }
                catch (Exception ex) { Warn($"{room.Tag} OnBeforeEnter 异常: {ex.Message}"); }
            };
        }

        IVirtualWorld world = await core!.EnsureAsync(req, ct).ConfigureAwait(false);
        room.World = world;
        Info($"{room.Tag} 世界已就绪（{world.DisplayName}）");
        return world;
    }

    // ★ 加载期（BeforeGlobalInitialize）TShock.Log 可能还没就绪：日志一律吞异常，绝不能因为打日志把宿主带崩
    private static void SafeLog(string level, string s)
    {
        try
        {
            switch (level)
            {
                case "warn": TShock.Log.Warning(s); break;
                case "error": TShock.Log.Error(s); break;
                default: TShock.Log.Info(s); break;
            }
        }
        catch { }
    }

    /// <summary>世界/服务名净化：只留字母数字下划线（Windows 文件名不允许 : * ? 等）。</summary>
    private static string SanitizeFile(string raw)
    {
        StringBuilder sb = new(raw.Length);
        foreach (char ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        }
        return sb.ToString();
    }

    private void Info(string s) => SafeLog("info", s);
    private void Warn(string s) => SafeLog("warn", s);
    private void Error(string s) => SafeLog("error", s);
}
