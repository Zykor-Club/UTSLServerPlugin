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

    public EdgeStitchConfig Config { get; set; } = new();

    /// <summary>每个世界每 tick 调用一次（Game.PostUpdate 在 UTSL 是按世界触发的）。</summary>
    public void OnPostUpdate(ServerContext server)
    {
        FinishPending();

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

            if (east is not null && pushingEast && player.position.X >= worldRightPx - bandPx)
            {
                target = east;
                targetX = bandPx;
                direction = "东";
            }
            else if (west is not null && pushingWest && player.position.X <= bandPx)
            {
                target = west;
                targetX = west.Main.maxTilesX * TilePx - bandPx - player.width;
                direction = "西";
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
            int seamX = (int)(targetX / TilePx);
            int seamY = (int)((player.position.Y + player.height) / TilePx);
            if (!TryFindSeamSpawn(target, seamX, seamY, out int spawnX, out int spawnY))
            {
                WarnThrottled("nospawn:" + target.Name,
                    "[EdgeStitch] 在 " + target.Name + " 的接缝附近找不到合法出生点，本次不传送（避免掉到世界出生点）");
                continue;
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

            TShock.Log.Info("[EdgeStitch] " + player.name + " 从 " + server.Name + " 的" + direction + "边缘 → " + target.Name
                + "（接缝 x≈" + (targetX / TilePx).ToString("F0") + " 格，落点图格 " + spawnX + "," + spawnY + "）");
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
    private static bool TryFindSeamSpawn(ServerContext server, int tileX, int tileY, out int spawnX, out int spawnY)
    {
        spawnX = -1;
        spawnY = -1;

        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;

        // 只传送到相邻世界的【对应边缘】，所以从接缝列向左右各找 24 格即可。
        for (int dx = 0; dx <= 24; dx++)
        {
            for (int dir = 1; dir >= -1; dir -= 2)
            {
                if (dx == 0 && dir < 0)
                {
                    continue;   // dx=0 只试一次
                }

                int x = tileX + (dx * dir);
                if (x < 2 || x > maxX - 3)
                {
                    continue;
                }

                // 整列扫描。注意不能用 ±30 格的高度窗口：两侧边缘的地形高度可能差很多
                //（例如一边是海滩、另一边是海底），窗口太小就会一个合法点都找不到。
                // 液体不是 active() 的方块，所以"水面以下"也会被正确当作可站立位置处理。
                for (int y = 2; y < maxY - 3; y++)
                {
                    TileData body = server.Main.tile[x, y];
                    TileData head = server.Main.tile[x, y - 1];
                    TileData floor = server.Main.tile[x, y + 1];

                    if (body.active() || head.active())
                    {
                        continue;   // 身体两格必须是空的
                    }
                    if (!floor.active() || !server.Main.tileSolid[floor.type])
                    {
                        continue;   // 脚下必须有实心方块
                    }

                    spawnX = x;
                    spawnY = y;
                    return true;
                }
            }
        }

        return false;
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
