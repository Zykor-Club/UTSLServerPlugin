using EdgeStitch.Config;
using Microsoft.Xna.Framework;
using Terraria;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Servers;

namespace EdgeStitch.Core;

/// <summary>
/// 多世界边缘无缝拼接的核心逻辑。
///
/// 落点是怎么控制的（读 UnifiedServerCoordinator.TransferPlayerToServer 源码得到）：
///   player = players[plr] = from.Main.player[plr];   // 同一个 Player 实例整体搬到目标世界
///   to.Main.player[plr] = player;
///   player.Spawn(to, PlayerSpawnContext.SpawningIntoWorld);   // ← 这一步决定落点
///   Player.Spawn() 用的是 player.SpawnX/SpawnY（无效才退回世界出生点）。
///
/// 所以正确做法是【换乘前把 SpawnX/SpawnY 临时指向接缝】，让出生本身就发生在接缝处；
/// 换乘完成后（同一个 Player 对象已经在新世界里）再恢复原本的出生点。
/// 不要用「落地后每 tick 纠正位置」——那是在跟登录流程对抗。
/// </summary>
public sealed class EdgeTransferManager
{
    private const float TilePx = 16f;

    private sealed class PendingTransfer
    {
        public string TargetWorldName = "";
        public Vector2 Velocity;
        public int OriginalSpawnX;
        public int OriginalSpawnY;
        public int TargetWorldSpawnX;
        public int TargetWorldSpawnY;
        public long IssuedAt;
    }

    private readonly Dictionary<int, PendingTransfer> _pending = [];
    private readonly Dictionary<int, long> _cooldown = [];
    private readonly Dictionary<string, long> _lastWarn = [];

    private long _lastDebug;

    public EdgeStitchConfig Config { get; set; } = new();

    /// <summary>每个世界每 tick 调用一次（Game.PostUpdate 在 UTSL 是按世界触发的）。</summary>
    private long _lastUnify;

    public void OnPostUpdate(ServerContext server)
    {
        FinishPending();

        // 周期性把世界级状态（时间/天气/世界进度）统一到链上其它世界，
        // 否则各世界各自推进时间，会越走越不一样。
        if (Environment.TickCount64 - _lastUnify >= 2000)
        {
            _lastUnify = Environment.TickCount64;
            List<ServerContext> chainWorlds = [];
            foreach (string name in Config.WorldChain)
            {
                ServerContext? w = ResolveNeighbor(Config.WorldChain, IndexOf(name));
                if (w is not null)
                {
                    chainWorlds.Add(w);
                }
            }
            if (chainWorlds.Count >= 2)
            {
                WorldStateSync.UnifyChain(chainWorlds);
            }
        }

        List<string> chain = Config.WorldChain;
        if (chain.Count < 2)
        {
            return;
        }

        int index = IndexOf(server.Name);
        if (index < 0)
        {
            return;
        }

        ServerContext? west = ResolveNeighbor(chain, index - 1);
        ServerContext? east = ResolveNeighbor(chain, index + 1);
        if (west is null && east is null)
        {
            return;
        }

        float bandPx = Math.Max(1, Config.EdgeBandTiles) * TilePx;
        float worldRightPx = server.Main.maxTilesX * TilePx;
        int playerCount = Terraria.Main.maxPlayers;

        for (int who = 0; who < playerCount; who++)
        {
            Player? player = server.Main.player[who];
            if (player is null || !player.active || player.dead)
            {
                continue;
            }

            if (_cooldown.TryGetValue(who, out long until) && Environment.TickCount64 < until)
            {
                continue;
            }

            bool outward = !Config.RequireOutwardVelocity;
            // 用【输入标志】而不只是速度：顶着世界边界推时速度会被归零，只看 velocity 会漏判。
            bool pushingEast = outward || player.velocity.X > 0.01f || player.controlRight;
            bool pushingWest = outward || player.velocity.X < -0.01f || player.controlLeft;

            ServerContext? target = null;
            float targetX = 0f;
            string direction = "";

            // 抹海之后，边界是各世界自己的【陆地边缘】（虚空开始处），而不是世界边缘。
            // 没配置陆地边缘的世界退回旧行为（距世界边缘 EdgeBandTiles 格）。
            int margin = Math.Max(1, Config.TriggerMarginTiles);
            WorldLandEdges? here = Config.Resolve(server.Name);

            int eastLimitPx = here?.East is int myEast ? (myEast - margin) * 16 : (int)(worldRightPx - bandPx);
            int westLimitPx = here?.West is int myWest ? (myWest + margin) * 16 : (int)bandPx;

            // 诊断：接近任意边缘时（150 格内）每秒输出一次判定依据，
            // 用来定位"为什么没触发"。确认无误后可删。
            float nearPx = 150 * TilePx;
            if (player.position.X <= nearPx || player.position.X >= worldRightPx - nearPx)
            {
                if (Environment.TickCount64 - _lastDebug >= 1000)
                {
                    _lastDebug = Environment.TickCount64;
                    TShock.Log.Info($"[EdgeStitch][DBG] {player.name} x={player.position.X:F0}({player.position.X / TilePx:F0}格) "
                        + $"vx={player.velocity.X:F2} 右按={player.controlRight} 左按={player.controlLeft} | "
                        + $"世界={server.Name}(宽{server.Main.maxTilesX}) 西邻={(west?.Name ?? "无")} 东邻={(east?.Name ?? "无")} | "
                        + $"东阈值 x>={eastLimitPx / TilePx:F0}格 西阈值 x<={westLimitPx / TilePx:F0}格");
                }
            }


            // ★ 用【朝向那一侧的身体边缘】判定：向东走时 position.X 是碰撞箱左边缘，
            //   只比它会让玩家多贴 1~2 格才触发（实测约 1 秒的迟滞）。
            float eastEdgePx = player.position.X + player.width;
            if (east is not null && pushingEast && eastEdgePx >= eastLimitPx)
            {
                // 落在【对方】西侧陆地边缘的内侧
                target = east;
                if (Config.SliceDeltaTiles > 0)
                {
                    int srcTile = (int)(player.position.X / TilePx);
                    int destTile = Math.Clamp(srcTile - Config.SliceDeltaTiles, 43, east.Main.maxTilesX - 44);
                    targetX = destTile * TilePx;
                    direction = "东(切片Δ)";
                }
                else
                {
                    int destWest = Config.Resolve(east.Name)?.West ?? (int)(bandPx / TilePx);
                    targetX = (destWest + margin) * TilePx;
                    direction = "东";
                }
            }
            else if (west is not null && pushingWest && player.position.X <= westLimitPx)
            {
                // 落在【对方】东侧陆地边缘的内侧
                target = west;
                if (Config.SliceDeltaTiles > 0)
                {
                    int srcTile = (int)(player.position.X / TilePx);
                    int destTile = Math.Clamp(srcTile + Config.SliceDeltaTiles, 43, west.Main.maxTilesX - 44);
                    targetX = destTile * TilePx;
                    direction = "西(切片Δ)";
                }
                else
                {
                    float destEast = Config.Resolve(west.Name)?.East is int de ? de * TilePx : (west.Main.maxTilesX * TilePx - bandPx);
                    targetX = destEast - (margin * TilePx) - player.width;
                    direction = "西";
                }
            }

            if (target is null)
            {
                continue;
            }

            if (target.Main.maxTilesX != server.Main.maxTilesX || target.Main.maxTilesY != server.Main.maxTilesY)
            {
                WarnThrottled(server.Name + "->" + target.Name,
                    "[EdgeStitch] 世界 '" + server.Name + "' 与 '" + target.Name + "' 尺寸不同，无法无缝传送（需要同尺寸世界）");
                continue;
            }

            // 关键：把出生点临时挪到接缝，player.Spawn() 就会落在这里
            PendingTransfer pending = new()
            {
                TargetWorldName = target.Name,
                Velocity = player.velocity,
                OriginalSpawnX = player.SpawnX,
                OriginalSpawnY = player.SpawnY,
                TargetWorldSpawnX = target.Main.spawnTileX,
                TargetWorldSpawnY = target.Main.spawnTileY,
                IssuedAt = Environment.TickCount64,
            };
            // 落点必须是一个【能通过 Terraria 校验】的出生点，否则 SpawnX/SpawnY 会被清成 -1，
            // 结果就是掉到世界出生点（踩过）。而且客户端对自己的位置有权威性，
            // 事后改 position 会被客户端同步覆盖回来，所以只能让"出生"本身就落在正确位置。
            // ★ 位置保持换乘：两个世界是同一张大地图切出来的，同一大地图坐标处地形完全相同，
            //   所以【直接把玩家的图格坐标平移过去】就是唯一正确的落点 —— X 减 Δ、Y 原样照搬。
            //   不要再用"扫描找地表"那一套（实测会向内跑、还会挑到洞穴/空岛）。
            int srcTileX = (int)(player.position.X / TilePx);
            int srcTileY = (int)((player.position.Y + player.height) / TilePx);
            int seamX = srcTileX - Config.SliceDeltaTiles * (direction.StartsWith("西") ? -1 : 1);

            // ★ 保持"玩家离脚下的高度"不变地落到目标世界。
            //   接缝两侧只差 1 格，直接照搬 Y 有时会正好压在方块上，之前的兜底会向上挪 0~8 格，
            //   表现为"落点有时偏上、有时偏下"。改成按【两侧地表高度差】修正：
            //   目标落点 Y = 目标列地表 Y − (源列地表 Y − 玩家当前 Y)
            int srcGroundY = FindGroundY(server, srcTileX, srcTileY);
            int dstGroundY = FindGroundY(target, seamX, srcTileY);
            int seamY = (srcGroundY > 0 && dstGroundY > 0)
                ? Math.Clamp(dstGroundY - (srcGroundY - srcTileY), 2, target.Main.maxTilesY - 4)
                : srcTileY;

            int spawnX = seamX;
            int spawnY = seamY;

            // 校验：目标坐标若落在实心方块里（理论上不该发生，因为地形一致），
            // 只做【极小范围】的向上微调，绝不向内搜索。
            for (int nudge = 0; nudge <= 8; nudge++)
            {
                int ty = seamY - nudge;
                if (ty < 2 || ty >= target.Main.maxTilesY - 3)
                {
                    break;
                }
                if (seamX < 2 || seamX >= target.Main.maxTilesX - 3)
                {
                    break;
                }
                bool solid = target.Main.tile[seamX, ty].active() || target.Main.tile[seamX, ty + 1].active();
                if (!solid)
                {
                    spawnX = seamX;
                    spawnY = ty;
                    break;
                }
            }

            // ★ 关键：临时把【目标世界的世界出生点】挪到接缝。
            //   Player.Spawn() 在 SpawnX/SpawnY 无效时会走 Spawn_SetPositionAtWorldSpawn()，
            //   而那条路是拿世界出生点去 Spawn_GetPositionAtSpawn() 【搜索】合法站位，
            //   不像 SpawnX/SpawnY 那样会被 CheckSpawn 否决并清成 -1。
            //   社区那位服主的做法（把卫星世界出生点设在接壤的那条边）正是同一个机制。
            //   我们这里做成【临时】重定向，双向换乘都能用，也不会永久改变世界的重生点。
            target.Main.spawnTileX = spawnX;
            target.Main.spawnTileY = spawnY;

            // 同时清掉玩家自己的出生点（床），否则会走 SpawnX/SpawnY 那条被校验否决的路
            player.SpawnX = -1;
            player.SpawnY = -1;
            _pending[who] = pending;
            _cooldown[who] = Environment.TickCount64 + Math.Max(0, Config.CooldownMs);

            // 落点详查：把目标列的实际地形打出来（地表/玩家站位/脚下），一次定位"落得太深"这类问题
            string Terrain(int tx, int ty)
            {
                TileData foot = target.Main.tile[tx, Math.Max(2, ty + 1)];
                TileData body = target.Main.tile[tx, Math.Max(2, ty)];
                TileData head = target.Main.tile[tx, Math.Max(2, ty - 1)];
                return $"脚下={(foot.active() ? "实心" : "空")} 身体={(body.active() ? "实心" : "空")} 头={(head.active() ? "实心" : "空")}";
            }

            TShock.Log.Info("[EdgeStitch] " + player.name + " " + server.Name + "→" + target.Name + "（" + direction + "）"
                + " 源格 x=" + (player.position.X / TilePx).ToString("F0")
                + " 目标格 x=" + (targetX / TilePx).ToString("F0")
                + " 落点=" + spawnX + "," + spawnY
                + " | " + Terrain(spawnX, spawnY));

            // ★ 换乘前把【源世界】的世界级状态拷给目标世界：
            //   时间、天气、世界进度（Boss 旗标等）随之过去。
            //   玩家自身（buff/debuff/血量/背包）不用管 —— UTSL 迁移的是同一个 Player 对象。
            WorldStateSync.CopyWorldState(server, target);

            UnifiedServerCoordinator.TransferPlayerToServer((byte)who, target);

            // player.Spawn() 是同步调用，上面两个值已经用完，立刻还原
            target.Main.spawnTileX = pending.TargetWorldSpawnX;
            target.Main.spawnTileY = pending.TargetWorldSpawnY;
            player.SpawnX = pending.OriginalSpawnX;
            player.SpawnY = pending.OriginalSpawnY;
        }
    }

    /// <summary>
    /// 在接缝列附近找一个"脚下有实心方块、身体两格是空的"的合法出生位。
    /// 直接照搬 Terraria 的出生点语义，避免 SpawnX/SpawnY 被 CheckSpawn 判无效后清空。
    /// </summary>
    /// <summary>
    /// 找某一列上、离 hintY 最近的"地表"（该格空气 + 下一格实心）。
    /// 用于计算换乘前后玩家离地高度，保证落点高度稳定。
    /// </summary>
    private static int FindGroundY(ServerContext server, int x, int hintY)
    {
        if (server.Main.tile is null || x < 2 || x >= server.Main.maxTilesX - 3)
        {
            return -1;
        }
        int best = -1, bestDist = int.MaxValue;
        for (int y = 2; y < server.Main.maxTilesY - 3; y++)
        {
            TileData body = server.Main.tile[x, y];
            TileData floor = server.Main.tile[x, y + 1];
            if (!body.active() && floor.active())
            {
                int d = Math.Abs(y - hintY);
                if (d < bestDist) { bestDist = d; best = y; }
                if (d <= 2) { return y; }
            }
        }
        return best;
    }

    private static bool TryFindSeamSpawn(ServerContext server, int tileX, int preferredY, out int spawnX, out int spawnY)
    {
        spawnX = -1;
        spawnY = -1;

        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;

        // 朝向【世界中心】搜索：接缝在西边缘时内侧是 +x，东边缘时是 -x。
        int inward = tileX < (maxX / 2) ? 1 : -1;

        long bestDist = long.MaxValue;
        bool found = false;

        for (int dx = 0; dx <= 64; dx++)
        {
            int x = tileX + (dx * inward);
            if (x < 2 || x > maxX - 3)
            {
                break;
            }

            // 整列扫描，收集所有"脚下实心 + 身体两格空 + 上方无液体"的站位，
            // 然后挑【最接近玩家当前高度】的那个 —— 这样就不会落到空岛上。
            for (int y = 2; y < maxY - 3; y++)
            {
                ref TileData body = ref server.Main.tile[x, y];
                ref TileData head = ref server.Main.tile[x, y - 1];
                ref TileData floor = ref server.Main.tile[x, y + 1];

                if (!floor.active() || body.active() || head.active())
                {
                    continue;
                }

                // 水下的位置不要（世界边缘常见海洋，海底不算好落点）
                bool submerged = false;
                for (int y2 = 2; y2 <= y; y2++)
                {
                    if (server.Main.tile[x, y2].liquid > 100) { submerged = true; break; }
                }
                if (submerged)
                {
                    continue;
                }

                // ★ 主判据：贴近【世界地表线】(worldSurface)。
                //   空岛在地表线之上、洞穴在地表线之下，都会被这条判据自然排除。
                //   之所以不能依赖 preferredY（玩家当前高度）：UTSL 换乘迁移的是同一个 Player 对象，
                //   换过世界之后插件在源世界 tick 里读到它的坐标已经属于目标世界了，锚点不可靠（实测就是这样落到地下 144 格）。
                //   玩家高度只作为很小的修正量，用于在多个候选里挑最贴近直觉的那个。
                double penalty = Math.Abs(y - server.Main.worldSurface) * 1.0
                               + Math.Abs(y - preferredY) * 0.05;

                // 同一列优先靠近接缝（dx 小）
                penalty += dx * 0.5;

                if (penalty < bestDist)
                {
                    bestDist = (int)(penalty * 100);
                    spawnX = x;
                    spawnY = y;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 换乘后收尾：恢复被临时改写的出生点，并把跑动惯性还回去。
    /// （Player.Spawn() 会把速度清零，这一步是为了"冲过边界"的手感。）
    /// 换乘是同步完成的，下一 tick 处理即可，不需要依赖任何事件。
    /// </summary>
    private void FinishPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        List<int>? finished = null;

        foreach (KeyValuePair<int, PendingTransfer> kv in _pending)
        {
            PendingTransfer pending = kv.Value;
            if (now - pending.IssuedAt < 50)
            {
                continue;
            }

            (finished ??= []).Add(kv.Key);

            // 同一个 Player 实例已经搬到目标世界，用协调器按客户端索引取回来
            Player? migrated = UnifiedServerCoordinator.GetPlayer(kv.Key);
            if (migrated is null)
            {
                continue;
            }

            migrated.velocity = pending.Velocity;   // Spawn() 会把速度清零，这里还回跑动惯性
        }

        if (finished is not null)
        {
            foreach (int who in finished)
            {
                _pending.Remove(who);
            }
        }
    }

    public void ClearPlayer(int who)
    {
        _pending.Remove(who);
        _cooldown.Remove(who);
    }

    private int IndexOf(string name)
    {
        List<string> chain = Config.WorldChain;
        for (int i = 0; i < chain.Count; i++)
        {
            if (string.Equals(chain[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private ServerContext? ResolveNeighbor(List<string> chain, int index)
    {
        if (chain.Count == 0)
        {
            return null;
        }

        if (Config.Loop)
        {
            index = ((index % chain.Count) + chain.Count) % chain.Count;
        }
        else if (index < 0 || index >= chain.Count)
        {
            return null;
        }

        string name = chain[index];
        foreach (ServerContext server in UnifiedServerCoordinator.Servers)
        {
            if (server.IsRunning && string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return server;
            }
        }
        return null;
    }

    private void WarnThrottled(string key, string message)
    {
        long now = Environment.TickCount64;
        if (_lastWarn.TryGetValue(key, out long last) && now - last < 10_000)
        {
            return;
        }
        _lastWarn[key] = now;
        TShock.Log.Warning(message);
    }
}
