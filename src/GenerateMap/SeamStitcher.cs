using Terraria;
using UnifierTSL.Servers;

namespace GenerateMap;

/// <summary>
/// 抹海缝合：把接缝两侧的海洋区块【整块清空】，再把换乘边界挪到陆地边缘。
///
/// 为什么不"填海造陆"：那要重写几千个图格、还得合成地形 ✗。
/// 而只要把换乘边界挪到陆地边缘，玩家在陆地边缘就被传送走了，
/// 那片空白区【永远不会被走到】—— 所以直接清空即可。
/// </summary>
public static class SeamStitcher
{
    /// <summary>世界链顺序（与 EdgeStitch 的配置一致）：只有接缝侧的海洋需要抹，两端保留。</summary>
    public static readonly string[] Chain = ["West", "Dev", "East"];

    public static void EraseSeamOceans(ServerContext server, Action<string> log)
    {
        int idx = Array.FindIndex(Chain, n => string.Equals(n, server.Name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            log($"[抹海] 世界 {server.Name} 不在链中，跳过");
            return;
        }

        bool eraseEast = idx < Chain.Length - 1;   // 不是最后一个 → 东边是接缝
        bool eraseWest = idx > 0;                  // 不是第一个 → 西边是接缝

        log($"[抹海] === {server.Name}（西={eraseWest} 东={eraseEast}）===");

        if (eraseWest)
        {
            EraseEdge(server, fromWest: true, log);
        }
        if (eraseEast)
        {
            EraseEdge(server, fromWest: false, log);
        }
    }

    private static void EraseEdge(ServerContext server, bool fromWest, Action<string> log)
    {
        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;

        // 先量出海洋宽度（从边缘连续判水）
        int width = MeasureOceanWidth(server, fromWest);
        if (width <= 0)
        {
            log($"[抹海] {(fromWest ? "西" : "东")}边缘没有连续水体，跳过");
            return;
        }

        int cleared = 0;
        int chests = 0;

        for (int i = 0; i < width; i++)
        {
            int x = fromWest ? i : (maxX - 1 - i);

            for (int y = 2; y < maxY - 3; y++)
            {
                ref var tile = ref server.Main.tile[x, y];

                if (tile.active() && (tile.type == 21 || tile.type == 467))
                {
                    chests++;
                }

                if (tile.active() || tile.wall > 0 || tile.liquid > 0)
                {
                    tile.active(false);
                    tile.type = 0;
                    tile.wall = 0;
                    tile.liquid = 0;
                    cleared++;
                }
            }
        }

        log($"[抹海] {(fromWest ? "西" : "东")}边缘 清空 {width} 列 / {cleared} 个图格，删除宝箱 {chests} 个");
    }

    private static int MeasureOceanWidth(ServerContext server, bool fromWest)
    {
        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;
        int limit = Math.Min(2000, maxX / 2);
        int width = 0;

        for (int i = 0; i < limit; i++)
        {
            int x = fromWest ? i : (maxX - 1 - i);
            bool hasWater = false;

            for (int y = 2; y < maxY - 3; y++)
            {
                TileData tile = server.Main.tile[x, y];
                if (tile.liquid > 50)
                {
                    hasWater = true;
                }
                if (tile.active() && server.Main.tileSolid[tile.type])
                {
                    break;
                }
            }

            if (!hasWater)
            {
                break;   // 连续水体到此为止
            }
            width = i + 1;
        }

        return width;
    }
}
