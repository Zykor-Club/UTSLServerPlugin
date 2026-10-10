using Terraria;
using UnifierTSL.Servers;

namespace GenerateMap;

/// <summary>
/// 接缝剖面分析：输出每个世界【西边缘】和【东边缘】的地表高度曲线 + 地表方块类型分布。
///
/// 用途：在两两相邻世界之间量化「落差有多大、群系是否突变」，
/// 这是设计"渐变缝合"宽度的依据 —— 不靠目测。
/// </summary>
public static class SeamProfile
{
    private const int SampleStep = 4;   // 每隔 4 列采样一次

    public static void Dump(ServerContext server, Action<string> log)
    {
        int maxX = server.Main.maxTilesX;
        int band = Math.Clamp(maxX / 8, 32, 128);

        log($"[剖面] === 世界 {server.Name}（宽 {maxX} 高 {server.Main.maxTilesY}）===");
        DumpEdge(server, 0, band, "西边缘", log);
        DumpEdge(server, maxX - band, maxX, "东边缘", log);
    }

    private static void DumpEdge(ServerContext server, int fromX, int toX, string label, Action<string> log)
    {
        List<string> heights = [];
        Dictionary<ushort, int> tiles = [];

        for (int x = fromX; x < toX; x += SampleStep)
        {
            int surfaceY = -1;
            ushort surfaceType = 0;

            for (int y = 2; y < server.Main.maxTilesY - 3; y++)
            {
                TileData tile = server.Main.tile[x, y];
                if (tile.active() && server.Main.tileSolid[tile.type])
                {
                    surfaceY = y;
                    surfaceType = tile.type;
                    break;
                }
            }

            heights.Add($"{x}:{surfaceY}");
            if (surfaceType != 0)
            {
                tiles[surfaceType] = tiles.GetValueOrDefault(surfaceType) + 1;
            }
        }

        string top = string.Join(", ", tiles.OrderByDescending(kv => kv.Value).Take(6)
            .Select(kv => TileName(kv.Key) + "×" + kv.Value));

        log($"[剖面] {label} 地表高度(列:格) {string.Join(" ", heights)}");
        log($"[剖面] {label} 地表方块 {top}");
    }

    /// <summary>常见地表方块的中文名，未知就输出原始 ID。</summary>
    private static string TileName(ushort type) => type switch
    {
        0 => "土",
        1 => "石",
        2 => "草",
        23 => "腐化草",
        53 => "沙",
        57 => "黑檀石",
        59 => "灰烬?",
        60 => "丛林草",
        70 => "蘑菇草",
        109 => "神圣草",
        116 => "雪块?",
        147 => "冰雪",
        161 => "雪块",
        163 => "猩红草",
        199 => "血肉块",
        203 => "沙岩?",
        234 => "雪块",
        367 => "沙岩",
        368 => "硬化沙",
        _ => "#" + type,
    };
}
