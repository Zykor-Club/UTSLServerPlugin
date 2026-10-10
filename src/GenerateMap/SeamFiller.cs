using Terraria;
using UnifierTSL;
using UnifierTSL.Servers;

namespace GenerateMap;

/// <summary>
/// 填陆：抹海之后把空出来的区域填成【连续陆地】，让陆地一直长到世界边缘，
/// 这样接缝两侧就是"直接拼在一起"，而不是中间空出一片虚空。
///
/// 高度做法：从【邻居陆地边界的地表高度】（世界边缘处）线性渐变到【本地陆地边界的地表高度】，
/// 实测两侧落差都在 10 格以内，渐变非常平缓。
/// </summary>
public static class SeamFiller
{
    public static readonly string[] Chain = ["West", "Dev", "East"];

    private static readonly Dictionary<string, (int? West, int? East)> LandEdges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["West"] = (null, 3972),
        ["Dev"] = (274, 3955),
        ["East"] = (251, null),
    };

    public static void FillSeamLands(ServerContext server, Action<string> log)
    {
        int idx = Array.FindIndex(Chain, n => string.Equals(n, server.Name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            log($"[填陆] 世界 {server.Name} 不在链中，跳过");
            return;
        }

        // 自己量出被抹空的区域（地下完全没有实心方块 = 空列），不依赖硬编码边界，
        // 这样世界重新生成后依然可用。
        int westVoid = MeasureVoid(server, fromWest: true);
        int eastVoid = MeasureVoid(server, fromWest: false);

        log($"[填陆] === {server.Name}（西空列 {westVoid} / 东空列 {eastVoid}）===");

        // ---- 西侧：把 [0, westVoid) 填实 ----
        if (westVoid > 0 && idx > 0)
        {
            ServerContext? nb = Find(Chain[idx - 1]);
            if (nb is not null)
            {
                int nbEastVoid = MeasureVoid(nb, fromWest: false);
                int hNb = SurfaceY(nb, Math.Max(2, nb.Main.maxTilesX - 1 - nbEastVoid));   // 邻居东侧陆地边界
                int hLocal = SurfaceY(server, westVoid);                                   // 本地西侧陆地边界
                int hSeam = (hNb + hLocal) / 2;                                            // 边缘两侧必须等高
                ushort mat = SurfaceType(server, westVoid);
                Fill(server, 0, westVoid - 1, hSeam, hLocal, mat, log, $"西(接缝高 {hSeam})");
            }
        }

        // ---- 东侧：把 (maxX-1-eastVoid, maxX) 填实 ----
        if (eastVoid > 0 && idx < Chain.Length - 1)
        {
            ServerContext? nb = Find(Chain[idx + 1]);
            if (nb is not null)
            {
                int nbWestVoid = MeasureVoid(nb, fromWest: true);
                int hNb = SurfaceY(nb, Math.Min(nb.Main.maxTilesX - 3, nbWestVoid));        // 邻居西侧陆地边界
                int hLocal = SurfaceY(server, server.Main.maxTilesX - 1 - eastVoid);        // 本地东侧陆地边界
                int hSeam = (hNb + hLocal) / 2;
                ushort mat = SurfaceType(server, server.Main.maxTilesX - 1 - eastVoid);
                Fill(server, server.Main.maxTilesX - eastVoid, server.Main.maxTilesX - 1, hLocal, hSeam, mat, log, $"东(接缝高 {hSeam})");
            }
        }
    }

    /// <summary>从边缘向内数"地下完全没有实心方块"的列数 = 被抹空的宽度。</summary>
    private static int MeasureVoid(ServerContext server, bool fromWest)
    {
        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;
        int[] samples = [maxY / 4, maxY / 2, maxY - 60];
        int n = 0;

        for (int i = 0; i < maxX / 2; i++)
        {
            int x = fromWest ? i : (maxX - 1 - i);
            bool solid = false;

            foreach (int y in samples)
            {
                if (y < 2 || y >= maxY - 3)
                {
                    continue;
                }
                TileData tile = server.Main.tile[x, y];
                if (tile.active() && server.Main.tileSolid[tile.type])
                {
                    solid = true;
                    break;
                }
            }

            if (solid)
            {
                break;
            }
            n = i + 1;
        }

        return n;
    }

    private static void Fill(ServerContext server, int fromX, int toX, int hStart, int hEnd, ushort material, Action<string> log, string label)
    {
        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;
        int count = Math.Max(1, toX - fromX + 1);
        int filled = 0;

        for (int x = fromX; x <= toX; x++)
        {
            if (x < 2 || x > maxX - 3)
            {
                continue;
            }

            // ★ 先清空整列：保证可重复执行（否则会在旧地形上叠加），也把残留的水清掉
            for (int y = 2; y < maxY - 3; y++)
            {
                ref var old = ref server.Main.tile[x, y];
                if (old.active() || old.wall > 0 || old.liquid > 0)
                {
                    old.active(false);
                    old.type = 0;
                    old.wall = 0;
                    old.liquid = 0;
                }
            }

            double t = (double)(x - fromX) / count;
            int targetY = (int)Math.Round(hStart + ((hEnd - hStart) * t));

            // 表层用内陆材质，往下 2 格土、再 2 格石，避免悬空地表
            for (int y = targetY; y < maxY - 3; y++)
            {
                if (y < 2)
                {
                    continue;
                }

                ref var tile = ref server.Main.tile[x, y];
                tile.active(true);
                int depth = y - targetY;
                tile.type = depth == 0 ? material : (depth <= 3 ? (ushort)0 : (ushort)1);
                tile.liquid = 0;
                filled++;
            }
        }

        log($"[填陆] {label}侧 填充 {count} 列 / {filled} 图格（地表高度 {hStart} → {hEnd}）");
    }

    private static ServerContext? Find(string name)
    {
        foreach (ServerContext server in UnifiedServerCoordinator.Servers)
        {
            if (server.IsRunning && string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return server;
            }
        }
        return null;
    }

    private static int SurfaceY(ServerContext server, int x)
    {
        for (int y = 2; y < server.Main.maxTilesY - 3; y++)
        {
            TileData tile = server.Main.tile[x, y];
            if (tile.active() && server.Main.tileSolid[tile.type])
            {
                return y;
            }
        }
        return server.Main.maxTilesY / 4;
    }

    private static ushort SurfaceType(ServerContext server, int x)
    {
        for (int y = 2; y < server.Main.maxTilesY - 3; y++)
        {
            TileData tile = server.Main.tile[x, y];
            if (tile.active() && server.Main.tileSolid[tile.type])
            {
                return tile.type;
            }
        }
        return 0;
    }
}
