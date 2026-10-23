using System.Text.Json;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Servers;
using UTSL.MiniGame;
using UTSL.VirtualWorld;

namespace Parkour;

/// <summary>
/// 跑酷玩法：
///   · 世界 = 同尺寸空白世界（直接用现有空白世界文件当种子，秒建）
///   · 区域全部来自地图 JSON（/pk build 里用 /pk here 圈）
///   · 倒计时期间把玩家锁在"起点判定领域"内；开跑后判定终点与存档点
/// </summary>
internal sealed class ParkourGame : IMiniGame
{
    private readonly ParkourConfig config;
    private List<ParkourMap> maps = new();

    public ParkourGame(ParkourConfig config)
    {
        this.config = config;
        ReloadMaps();
    }

    public string Id => "parkour";
    public string DisplayName => "跑酷";
    public IReadOnlyList<IMapDefinition> Maps => maps;

    public IMapDefinition? FindMap(string mapId)
        => maps.FirstOrDefault(m => m.Id.Equals(mapId, StringComparison.OrdinalIgnoreCase));

    public bool Supports(MiniGameMode mode) => mode is MiniGameMode.Solo or MiniGameMode.Multi;

    internal void ReloadMaps()
    {
        List<ParkourMap> loaded = ParkourMapStore.LoadAll();

        // ★ 原地更新：房间/建造世界持有的是地图对象引用，替换列表会让它们继续用旧数据
        foreach (ParkourMap fresh in loaded)
        {
            ParkourMap? live = maps.FirstOrDefault(m => m.Id.Equals(fresh.Id, StringComparison.OrdinalIgnoreCase));
            if (live is null) { maps.Add(fresh); continue; }
            live.DisplayName = fresh.DisplayName;
            live.TilesX = fresh.TilesX; live.TilesY = fresh.TilesY;
            live.Capacity = fresh.Capacity;
            live.TimeLimitSeconds = fresh.TimeLimitSeconds;
            live.GraceSeconds = fresh.GraceSeconds;
            live.CountdownSeconds = fresh.CountdownSeconds;
            live.WorldTemplate = fresh.WorldTemplate;
            live.WorldFile = fresh.WorldFile;

            live.Activity = fresh.Activity; live.Start = fresh.Start;
            live.StartSpawn = fresh.StartSpawn; live.Finish = fresh.Finish;
            live.Saves = fresh.Saves;
            live.YTolerance = fresh.YTolerance;
            live.ScoreBase = fresh.ScoreBase; live.ScorePerSecond = fresh.ScorePerSecond; live.ScorePerSave = fresh.ScorePerSave;
        }
        // 删除文件的地图从内存移除
        maps.RemoveAll(m => !loaded.Any(l => l.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)));
        if (maps.Count > 0) { return; }

        ServerContext? main = UnifiedServerCoordinator.Servers.IsDefaultOrEmpty ? null : UnifiedServerCoordinator.Servers[0];
        ParkourMap blank = ParkourMap.CreateBlank("test", "测试赛道", main?.Main.maxTilesX ?? 0, main?.Main.maxTilesY ?? 0);
        maps.Add(blank);
        ParkourMapStore.Save(blank, out _);
    }

    // ==================== 世界构建 ====================

    public void ConfigureWorld(IMapDefinition def, VirtualWorldRequest request, TSPlayer player)
    {
        if (def is not ParkourMap map) { return; }

        // ① 地图自带世界文件（专用图）：直接拿它当种子，不做任何清空
        if (!string.IsNullOrWhiteSpace(map.WorldFile))
        {
            string wf = Path.Combine(Terraria.Main.WorldPath, map.WorldFile + ".wld");
            if (File.Exists(wf))
            {
                request.SeedWorldPath = wf;
                request.TemplateKey = null;
                request.OnLoaded = null;                 // 专用图不能被改
                TShock.Log.Info($"[跑酷] 使用专用地图世界: {Path.GetFileName(wf)}（{request.TilesX}x{request.TilesY}）");
                return;
            }
            TShock.Log.Warning($"[跑酷] 地图 {map.Id} 指定的世界文件不存在: {wf}，退回空白世界");
        }

        int w = request.TilesX, h = request.TilesY;
        string? blank = FindBlankSeed(w, h);

        if (blank is not null)
        {
            request.SeedWorldPath = blank;
            request.TemplateKey = null;
            request.OnLoaded = (ctx, _) => PrepareBlankWorld(ctx, map, needClear: false);
            TShock.Log.Info($"[跑酷] 直接用空白世界作种子: {Path.GetFileName(blank)}（不复制主世界、不清空）");
            return;
        }

        request.TemplateKey = "pk_blank";
        request.SeedWorldPath = UnifiedServerCoordinator.GetClientCurrentlyServer(player.Index)?.Main.worldPathName;
        request.OnLoaded = (ctx, justSeeded) => PrepareBlankWorld(ctx, map, needClear: justSeeded);
        TShock.Log.Info("[跑酷] 没找到同尺寸空白世界文件，退回 复制主世界+清空（只会发生一次）");
    }

    private static string? FindBlankSeed(int w, int h)
    {
        try
        {
            string dir = Path.Combine(Terraria.Main.WorldPath, "_vwc_templates");
            foreach (string name in new[] { $"pk_blank_seed_{w}x{h}.wld", $"pot_blank_{w}x{h}.wld", $"pk_blank_{w}x{h}.wld" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) { return p; }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[跑酷] 找空白种子失败: " + ex.Message); }
        return null;
    }

    private static void PrepareBlankWorld(ServerContext ctx, ParkourMap map, bool needClear)
    {
        try
        {
            if (needClear) { ctx.ClearCurrentWorld(); }

            TileRect area = map.HasStart ? map.StartRect : TileRect.Around(ctx.Main.maxTilesX / 2, ctx.Main.maxTilesY / 2, 6);
            int gy = area.Y1 + 2;
            for (int x = area.X0 - 2; x <= area.X1 + 2; x++)
            {
                for (int y = gy; y <= gy + 1; y++)
                {
                    if (x < 0 || y < 0 || x >= ctx.Main.maxTilesX || y >= ctx.Main.maxTilesY) { continue; }
                    ref var tile = ref ctx.Main.tile[x, y];
                    tile.ClearEverything();
                    tile.active(true);
                    tile.type = Terraria.ID.TileID.Stone;
                }
            }
            ctx.Main.spawnTileX = Math.Clamp((area.X0 + area.X1) / 2, 2, ctx.Main.maxTilesX - 3);
            ctx.Main.spawnTileY = Math.Clamp(gy - 1, 2, ctx.Main.maxTilesY - 3);
            try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); } catch { }

            TShock.Log.Info($"[跑酷] 空白场地就绪：平台 {area} 出生点 ({ctx.Main.spawnTileX},{ctx.Main.spawnTileY}) 清空={needClear}");
        }
        catch (Exception ex) { TShock.Log.Warning("[跑酷] 准备空白场地失败: " + ex.Message); }
    }

    /// <summary>
    /// 进人前：① 把世界出生点搬到【起点中心】——客户端加载完世界会自然站在那儿，
    /// 比事后补发传送可靠得多；② 空白世界顺手补一段起点平台（专用图不动一格）。
    /// </summary>
    public void OnBeforeEnter(IMiniGameRoom room, ServerContext context)
    {
        if (room.Map is not ParkourMap map || !map.HasStart) { return; }
        try
        {
            ParkourRoomState st = room.GetState<ParkourRoomState>();
            if (st.SpawnResolved && st.SpawnRect != map.StartRect)
            {
                st.SpawnResolved = false;    // 起点被重圈过 → 重新找空地
            }
            if (!st.SpawnResolved)
            {
                (int fx, int fy) = FindClearSpawn(context, map.StartRect);
                st.SpawnX = fx; st.SpawnY = fy; st.SpawnResolved = true;
                st.SpawnRect = map.StartRect;
                TShock.Log.Info($"[跑酷] 起点区域内找到 3x3 空地：({fx},{fy})（矩形 {map.StartRect}）");
            }
            int sx = st.SpawnX, sy = st.SpawnY;
            int clampedX = Math.Clamp(sx, 2, context.Main.maxTilesX - 3);
            int clampedY = Math.Clamp(sy, 2, context.Main.maxTilesY - 3);
            bool spawnChanged = context.Main.spawnTileX != clampedX || context.Main.spawnTileY != clampedY;
            context.Main.spawnTileX = clampedX;
            context.Main.spawnTileY = clampedY;

            // 只在出生点真的变了才落盘（8400x2400 序列化约 1 秒，不能每次进入都存）
            if (spawnChanged)
            {
                try { context.WorldFile.SaveWorld(resetTime: false, useTemps: false); }
                catch (Exception ex) { TShock.Log.Warning("[跑酷] 保存世界出生点失败: " + ex.Message); }
                TShock.Log.Info($"[跑酷] 世界出生点已设为 ({clampedX},{clampedY}) 并落盘");
            }

            // 只有空白世界才铺平台；专用地图（有 worldFile）一个方块都不改
            if (!string.IsNullOrWhiteSpace(map.WorldFile)) { return; }

            TileRect s = map.StartRect;
            int gy = s.Y1 + 2;
            for (int x = s.X0 - 2; x <= s.X1 + 2; x++)
            {
                for (int y = gy; y <= gy + 1; y++)
                {
                    if (x < 0 || y < 0 || x >= context.Main.maxTilesX || y >= context.Main.maxTilesY) { continue; }
                    ref var tile = ref context.Main.tile[x, y];
                    tile.ClearEverything();
                    tile.active(true);
                    tile.type = Terraria.ID.TileID.Stone;
                }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[跑酷] 设置起点出生点失败: " + ex.Message); }
    }

    // ==================== 房间钩子 ====================

    public void OnRoundStart(IMiniGameRoom room)
    {
        // ★ 房间会被复用：每局开始必须把"上一局的进度/完赛记录"清干净，
        //   否则第二次进房间时终点被当成已完赛、存档点被当成"不比旧进度大"而全部不触发。
        ParkourRoomState st = room.GetState<ParkourRoomState>();
        st.FinishTicks.Clear();
        st.Progress.Clear();
        st.JustDied.Clear();

        if (room.Map is not ParkourMap map || !map.HasStart) { return; }

        (int sx, int sy) = SpawnOf(room, map);
        TShock.Log.Info($"[跑酷] 放起点 ({sx},{sy})，玩家数={room.Players.Count}");
        foreach (int idx in room.Players)
        {
            TSPlayer? p = TShock.Players[idx];
            if (p is not { Active: true }) { continue; }
            // ★ 传送刚发生时客户端还在加载世界数据，会把人摆回世界出生点；
            //   所以在进房后的几个时间点补发（每次都在房间世界线程上执行）。
            PlaceAtStart(room, p, sx, sy);   // 只放一次；不再补发（补发会在倒计时里把人反复拽回一点）
        }
    }

    public void OnRaceStarted(IMiniGameRoom room)
    {
        foreach (int idx in room.Players)
        {
            TSPlayer? p = TShock.Players[idx];
            if (p is { Active: true }) { p.SendSuccessMessage("[跑酷] 开始！冲终点。"); }
        }
    }

    public void OnPlayerJoined(IMiniGameRoom room, int playerIndex)
    {
        TSPlayer? p = TShock.Players[playerIndex];
        if (p is not { Active: true }) { return; }
        p.SendInfoMessage($"[跑酷] 已进入「{room.Map.DisplayName}」");

        try
        {
            ParkourRoomState st0 = room.GetState<ParkourRoomState>();
            st0.OrigSpawn[playerIndex] = (p.TPlayer.SpawnX, p.TPlayer.SpawnY);
        }
        catch { }

        // 建造/单人：只要图里有起点，就把人放过去（作者圈区域时能直接站在起点上）
        if (room.Map is ParkourMap m && m.HasStart && room.State == MiniGameRoomState.Preparing)
        {
            (int sx, int sy) = StartPoint(m);
            PlaceAtStart(room, p, sx, sy);
        }
    }

    public void OnPlayerLeft(IMiniGameRoom room, int playerIndex, MiniGameLeaveReason reason)
    {
        ParkourRoomState st = room.GetState<ParkourRoomState>();
        st.JustDied.Remove(playerIndex);
        st.Progress.Remove(playerIndex);

        // 还原玩家原来的重生点（否则 SSC 会把跑酷坐标写进角色存档）
        if (st.OrigSpawn.Remove(playerIndex, out (int X, int Y) origin))
        {
            TSPlayer? p = TShock.Players[playerIndex];
            if (p is { Active: true })
            {
                try { p.TPlayer.SpawnX = origin.X; p.TPlayer.SpawnY = origin.Y; } catch { }
            }
        }
    }

    public void Tick(IMiniGameRoom room, long tick)
    {
        if (room.Map is not ParkourMap map) { return; }
        if (room.World?.Context is not ServerContext roomCtx) { return; }

        bool preparing = room.State == MiniGameRoomState.Preparing;   // 倒计时中
        ParkourRoomState st = room.GetState<ParkourRoomState>();
        int tol = Math.Max(2, map.YTolerance);
        (int sx, int sy) = SpawnOf(room, map);

        foreach (int idx in room.Players)
        {
            TSPlayer? p = TShock.Players[idx];
            if (p is not { Active: true }) { continue; }

            ServerContext? here = UnifiedServerCoordinator.GetClientCurrentlyServer(idx);
            bool inRoom = here is not null && ReferenceEquals(here, roomCtx);

            int x = p.TileX, y = p.TileY;

            if (!inRoom) { continue; }

            // ===== 死亡 → 复活：把人收回"最后一个存档点"（原生复活位置不可靠，自己接管）=====
            if (p.Dead) { st.JustDied.Add(idx); continue; }
            if (st.JustDied.Remove(idx))
            {
                (int rx, int ry) = RespawnPointOf(map, st, idx);
                TeleportInWorld(room, p, rx, ry);
                p.SendInfoMessage($"[跑酷] 已回到{(st.Progress.TryGetValue(idx, out int pr) && pr > 0 ? $"存档点 {pr}" : "起点")}");
                continue;
            }

            // ===== 倒计时：锁在起点判定领域内 =====
            if (preparing)
            {
                if (!map.HasStart) { continue; }
                if (!map.StartRect.ContainsWithTolerance(x, y, tol))
                {
                    // 推回"矩形内离你最近的点"，而不是固定出生点 → 区域内可以自由走动
                    TileRect sr = map.StartRect;
                    int nx = sr.Width <= 2 ? (sr.X0 + sr.X1) / 2 : Math.Clamp(x, sr.X0 + 1, sr.X1 - 1);
                    int ny = sr.Height <= 2 ? (sr.Y0 + sr.Y1) / 2 : Math.Clamp(y, sr.Y0 + 1, sr.Y1 - 1);
                    TeleportInWorld(room, p, nx, ny);

                    long now = Environment.TickCount64;
                    if (now - _lastPullMs > 1000)
                    {
                        _lastPullMs = now;
                        p.SendInfoMessage("[跑酷] 倒计时结束前只能在起点区域内活动");
                    }
                }
                if (tick % 300 == 0)
                {
                    TShock.Log.Info($"[跑酷][诊断] 倒计时 remaining={room.CountdownTicksLeft} 玩家({x},{y}) 出生({sx},{sy}) 矩形{map.StartRect}");
                }
                continue;
            }

            // ===== 越界：出了活动区就送回"上一个存档点（没则起点）" =====
            if (map.Activity.Length == 4 && !map.ActivityRect.ContainsWithTolerance(x, y, 2))
            {
                (int ox, int oy) = RespawnPointOf(map, st, idx);
                TeleportInWorld(room, p, ox, oy);
                long n = Environment.TickCount64;
                if (n - _lastOobMs > 1000)
                {
                    _lastOobMs = n;
                    p.SendInfoMessage("[跑酷] 越界，已送回上一个存档点");
                }
                continue;
            }

            // ===== 开跑后：终点 / 存档点 =====
            if (st.FinishTicks.ContainsKey(idx) || !map.HasFinish) { continue; }

            if (map.FinishRect.ContainsWithTolerance(x, y, tol))
            {
                st.FinishTicks[idx] = tick;
                room.RequestFinish(idx);
                p.SendSuccessMessage($"[跑酷] 到终点！用时 {FormatTicks(tick)}");
                continue;
            }

            foreach (SavePoint sp in map.Saves)
            {
                if (!sp.Rect.ContainsWithTolerance(x, y, tol)) { continue; }
                int old = st.Progress.TryGetValue(idx, out int cur) ? cur : -1;
                if (sp.Index > old)
                {
                    st.Progress[idx] = sp.Index;
                    room.SetProgress(idx, sp.Index);
                    try { p.TPlayer.SpawnX = sp.SpawnX; p.TPlayer.SpawnY = sp.SpawnY; } catch { }   // ★ 死亡后回这个存档点
                    p.SendInfoMessage($"[跑酷] 存档点 {sp.Index} 已记录（死亡将回到这里）");
                }
            }
        }
    }

    public IReadOnlyList<ScoreEntry> BuildResults(IMiniGameRoom room)
    {
        if (room.Map is not ParkourMap map) { return Array.Empty<ScoreEntry>(); }

        ParkourRoomState st = room.GetState<ParkourRoomState>();
        var finished = new List<(int idx, long tick, int progress, int score)>();
        foreach ((int idx, long tick) in st.FinishTicks)
        {
            int progress = st.Progress.TryGetValue(idx, out int pr) ? pr : -1;
            double seconds = tick / 60.0;
            int score = Math.Max(0, (int)(map.ScoreBase - map.ScorePerSecond * seconds))
                        + map.ScorePerSave * Math.Max(0, progress);
            finished.Add((idx, tick, progress, score));
        }

        finished.Sort((a, b) => a.tick.CompareTo(b.tick));

        var list = new List<ScoreEntry>(finished.Count);
        for (int i = 0; i < finished.Count; i++)
        {
            (int idx, long tick, int progress, int score) = finished[i];
            TSPlayer? p = TShock.Players[idx];
            list.Add(new ScoreEntry(
                GameId: Id,
                MapId: map.Id,
                Mode: room.Mode == MiniGameMode.Solo ? "solo" : "multi",
                Season: config.Season,
                PlayerKey: p?.Account?.ID.ToString() ?? ("idx:" + idx),
                PlayerName: p?.Name ?? ("player#" + idx),
                Metric: tick,
                Score: score,
                Rank: i + 1,
                RoomId: room.RoomId,
                ExtrasJson: JsonSerializer.Serialize(new { saves = progress, ticks = tick })));
        }
        return list;
    }

    private long _lastPullMs;
    private long _lastOobMs;

    /// <summary>
    /// 把玩家放到起点。
    /// ★ 客户端进新世界时用的是【玩家自己的 SpawnX/SpawnY】（床/回城点），不是世界出生点，
    ///   所以这里三件事一起做：写 TPlayer.Spawn* → Spawn() → 直接传送。
    /// </summary>
    private static void PlaceAtStart(IMiniGameRoom room, TSPlayer player, int sx, int sy)
    {
        // 玩家自己的重生点 = 起点（死亡后回这里；过存档点会被改成存档点）
        try
        {
            player.TPlayer.SpawnX = sx;
            player.TPlayer.SpawnY = sy;
        }
        catch { }

        TeleportInWorld(room, player, sx, sy);
    }

    /// <summary>把玩家传到房间世界里的某格——必须在【该世界】的更新线程上执行，否则客户端不会生效。</summary>
    private static void TeleportInWorld(IMiniGameRoom room, TSPlayer player, int tileX, int tileY)
    {
        void Do()
        {
            try { player.Teleport(tileX * 16f, tileY * 16f); }
            catch (Exception ex) { TShock.Log.Warning($"[跑酷] 传送失败: {ex.Message}"); }
        }

        if (room.World?.Context is ServerContext ctx)
        {
            if (ctx.Dispatcher.CheckAccess()) { Do(); }
            else { ctx.Dispatcher.Post(Do); }
            return;
        }
        Do();
    }

    /// <summary>
    /// 在起点区域内找一个 3×3 能站人的空地（脚下有实心、上方 3×3 空），优先靠近矩形中心。
    /// 找不到就退回矩形中心。
    /// </summary>
    private static (int x, int y) FindClearSpawn(ServerContext ctx, TileRect area)
    {
        int cx = (area.X0 + area.X1) / 2;
        int cy = (area.Y0 + area.Y1) / 2;

        bool Clear(int x, int y)
        {
            if (x - 1 < 1 || x + 1 >= ctx.Main.maxTilesX - 1) { return false; }
            if (y - 2 < 1 || y + 1 >= ctx.Main.maxTilesY - 1) { return false; }
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -2; dy <= 0; dy++)
                {
                    if (ctx.Main.tile[x + dx, y + dy].active()) { return false; }   // 身体要空
                }
                if (!ctx.Main.tile[x + dx, y + 1].active()) { return false; }        // 脚下要实
            }
            return true;
        }

        // 以中心为起点向外绕圈找（限制迭代次数，避免大地图卡住）
        int maxR = Math.Min(Math.Max(area.Width, area.Height) / 2 + 2, 64);   // 别在超大矩形上做百万次扫描
        for (int r = 0; r <= maxR; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                foreach (int dy in (r == 0 ? new[] { 0 } : new[] { -r, r }))
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < area.X0 || x > area.X1 || y < area.Y0 || y > area.Y1) { continue; }
                    if (Clear(x, y)) { return (x, y); }
                }
            }
            for (int dy = -r + 1; dy <= r - 1; dy++)
            {
                foreach (int dx in new[] { -r, r })
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < area.X0 || x > area.X1 || y < area.Y0 || y > area.Y1) { continue; }
                    if (Clear(x, y)) { return (x, y); }
                }
            }
        }
        return (cx, cy);
    }

    /// <summary>复活点：过了存档点就回该存档点，否则回起点。</summary>
    private static (int x, int y) RespawnPointOf(ParkourMap map, ParkourRoomState st, int playerIndex)
    {
        int progress = st.Progress.TryGetValue(playerIndex, out int pr) ? pr : -1;
        if (progress > 0)
        {
            SavePoint? sp = map.Saves.FirstOrDefault(s => s.Index == progress);
            if (sp is not null) { return (sp.SpawnX, sp.SpawnY); }
        }
        return st.SpawnResolved ? (st.SpawnX, st.SpawnY) : StartPoint(map);
    }

    /// <summary>取房间的出生点（首次进入时在起点区域内解析 3×3 空地）。</summary>
    private static (int x, int y) SpawnOf(IMiniGameRoom room, ParkourMap map)
    {
        ParkourRoomState st = room.GetState<ParkourRoomState>();
        if (st.SpawnResolved) { return (st.SpawnX, st.SpawnY); }

        (int x, int y) fallback = StartPoint(map);
        if (room.World?.Context is not ServerContext ctx || !map.HasStart) { return fallback; }

        (int x, int y) found = FindClearSpawn(ctx, map.StartRect);
        st.SpawnX = found.x;
        st.SpawnY = found.y;
        st.SpawnResolved = true;
        TShock.Log.Info($"[跑酷] 起点区域内找到 3x3 空地：({found.x},{found.y})（矩形 {map.StartRect}）");
        return found;
    }

    private static (int x, int y) StartPoint(ParkourMap map)
    {
        int cx = (map.StartRect.X0 + map.StartRect.X1) / 2;
        int cy = (map.StartRect.Y0 + map.StartRect.Y1) / 2;

        if (map.StartSpawn.Length != 2) { return (cx, cy); }

        int x = map.StartSpawn[0], y = map.StartSpawn[1];
        // 贴边（离中心超过 60% 半宽/半高）就按中心算，避免"看起来不在中心"
        int hw = Math.Max(1, (map.StartRect.X1 - map.StartRect.X0) / 2);
        int hh = Math.Max(1, (map.StartRect.Y1 - map.StartRect.Y0) / 2);
        if (Math.Abs(x - cx) > hw * 0.6 || Math.Abs(y - cy) > hh * 0.6) { return (cx, cy); }
        return (x, y);
    }

    private static string FormatTicks(long ticks)
    {
        double s = ticks / 60.0;
        int m = (int)(s / 60);
        return m > 0 ? $"{m}:{s - m * 60:00.000}" : $"{s:0.000}s";
    }
}

/// <summary>玩法侧的每房间状态：完赛 tick 与存档点进度。</summary>
internal sealed class ParkourRoomState
{
    public readonly Dictionary<int, long> FinishTicks = new();
    public readonly Dictionary<int, int> Progress = new();

    /// <summary>刚死过、还没被我们收回的玩家（死亡→复活 的边沿检测）。</summary>
    public readonly HashSet<int> JustDied = new();

    /// <summary>进房前的玩家重生点（离开时还原，避免把 SSC 角色存档写坏）。</summary>
    public readonly Dictionary<int, (int X, int Y)> OrigSpawn = new();

    /// <summary>在起点区域内找到的 3×3 空地（解析一次，之后复用）。</summary>
    public bool SpawnResolved;
    public int SpawnX;
    public int SpawnY;
    public TileRect SpawnRect;
}