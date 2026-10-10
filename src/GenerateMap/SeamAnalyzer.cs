using Terraria;
using UnifierTSL.Servers;

namespace GenerateMap;

/// <summary>
/// 抹海缝合前的【量化分析】：
///   - 每条边缘的海洋区范围（从边缘往内多少格才到陆地）
///   - 海洋区里的宝箱数量与所在列（这些列不能填掉）
///   - 内陆起始处的地表高度与材质（抹海后要承接的目标）
///
/// 先出数据再动手 —— 前面几轮反复返工的教训就是"凭猜测实现"。
/// </summary>
public static class SeamAnalyzer
{
    private const int ScanLimit = 2000;      // 最多往里扫多少列
    private const ushort ChestTile = 21;     // 宝箱
    private const ushort SandTile = 53;      // 沙（海洋/沙滩的地表特征）

    public static void Dump(ServerContext server, Action<string> log)
    {
        log($"[分析] === 世界 {server.Name}（宽 {server.Main.maxTilesX} 高 {server.Main.maxTilesY}）===");
        ScanEdge(server, fromWest: true, log);
        ScanEdge(server, fromWest: false, log);
    }

    private static void ScanEdge(ServerContext server, bool fromWest, Action<string> log)
    {
        int maxX = server.Main.maxTilesX;
        int limit = Math.Min(ScanLimit, maxX / 2);

        int oceanEndX = -1;          // 从外往内，最后一个"海洋特征"列
        int chestCount = 0;
        List<int> chestCols = [];
        int inlandY = -1;
        ushort inlandType = 0;

        for (int i = 0; i < limit; i++)
        {
            int x = fromWest ? i : (maxX - 1 - i);
            (int surfaceY, ushort type, bool hasWater) = SampleColumn(server, x);

            // 海洋特征：只看"地表之上有水"。
            // 不能把"地表是沙"也算进来 —— 沙漠和地下沙层会让判定一路吃到内陆（实测误判约 1900 列）。
            bool oceanLike = hasWater;

            if (oceanLike)
            {
                oceanEndX = x;

                for (int y = 2; y < server.Main.maxTilesY - 3; y++)
                {
                    TileData tile = server.Main.tile[x, y];
                    if (tile.active() && tile.type == ChestTile)
                    {
                        chestCount++;
                        if (chestCols.Count < 24)
                        {
                            chestCols.Add(x);
                        }
                        break;
                    }
                }
            }
            else
            {
                // ★ 关键：海洋是【从边缘起连续的水体】，遇到第一列没有水就到此为止。
                //   之前写成"取最内侧有水的列"，结果被内陆零散的水塘带偏（实测误判到 1877 列）。
                if (oceanEndX < 0)
                {
                    inlandY = surfaceY;
                    inlandType = type;
                }
                break;
            }
        }

        string label = fromWest ? "西边缘" : "东边缘";
        int width = oceanEndX < 0 ? 0 : (fromWest ? oceanEndX + 1 : maxX - oceanEndX);

        log($"[分析] {label}: 海洋区 {width} 列（最内侧列 x={oceanEndX}）；"
            + $"内陆起始 地表 y={inlandY} 材质 {TileName(inlandType)}");
        log($"[分析] {label}: 海洋区内宝箱 {chestCount} 个，所在列 [{string.Join(",", chestCols)}]");
    }

    private static (int surfaceY, ushort type, bool hasWater) SampleColumn(ServerContext server, int x)
    {
        int maxY = server.Main.maxTilesY;
        int surfaceY = -1;
        ushort type = 0;
        bool hasWater = false;

        for (int y = 2; y < maxY - 3; y++)
        {
            TileData tile = server.Main.tile[x, y];

            // 先看地表【之上】有没有水 —— 那才是海洋特征（地下水池不算）
            if (tile.liquid > 50)
            {
                hasWater = true;
            }

            if (tile.active() && server.Main.tileSolid[tile.type])
            {
                surfaceY = y;
                type = tile.type;
                break;
            }
        }

        return (surfaceY, type, hasWater);
    }

    private static string TileName(ushort type) => type switch
    {
        0 => "土",
        1 => "石",
        2 => "草",
        23 => "腐化草",
        53 => "沙",
        60 => "丛林草",
        70 => "蘑菇草",
        109 => "神圣草",
        147 => "冰雪",
        161 => "雪块",
        163 => "猩红草",
        199 => "血肉块",
        _ => "#" + type,
    };
}
