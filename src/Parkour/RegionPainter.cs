using TShockAPI;
using UnifierTSL.Servers;

namespace Parkour;

/// <summary>
/// 区域可视化（照 RegionView 的做法）：临时把边界图格换成假图格 → 只发给该玩家 → 立刻还原。
/// 只有在【该世界线程】上调用才安全。
/// </summary>
internal static class RegionPainter
{
    private const int MaxRun = 200;

    public static void Draw(ServerContext ctx, TSPlayer player, TileRect r, byte paint, int viewRange)
    {
        if (!ctx.Dispatcher.CheckAccess()) { ctx.Dispatcher.Post(() => Draw(ctx, player, r, paint, viewRange)); return; }
        if (!player.Active) { return; }

        int px = player.TileX, py = player.TileY;
        int x0 = Math.Max(r.X0, px - viewRange), x1 = Math.Min(r.X1, px + viewRange);
        int y0 = Math.Max(r.Y0, py - viewRange), y1 = Math.Min(r.Y1, py + viewRange);
        if (x0 > x1 || y0 > y1) { return; }

        // 上下边（各一条横线）、左右边（各一条竖线），只画落在视野内的那一段
        if (r.Y0 >= y0 && r.Y0 <= y1) { Run(ctx, player, x0, r.Y0, x1 - x0 + 1, 1, paint); }
        if (r.Y1 >= y0 && r.Y1 <= y1) { Run(ctx, player, x0, r.Y1, x1 - x0 + 1, 1, paint); }
        if (r.X0 >= x0 && r.X0 <= x1) { Run(ctx, player, r.X0, y0, 1, y1 - y0 + 1, paint); }
        if (r.X1 >= x0 && r.X1 <= x1) { Run(ctx, player, r.X1, y0, 1, y1 - y0 + 1, paint); }
    }

    /// <summary>在 (x,y) 起画 w×h 的边框条：假图格 → 发该玩家 → 还原。</summary>
    private static void Run(ServerContext ctx, TSPlayer player, int x, int y, int w, int h, byte paint)
    {
        int total = w * h;
        if (total <= 0) { return; }

        int step = Math.Min(Math.Max(w, h), MaxRun);
        for (int off = 0; off < Math.Max(w, h); off += step)
        {
            int cx = x + (w >= h ? off : 0);
            int cy = y + (h > w ? off : 0);
            int cw = w >= h ? Math.Min(step, x + w - cx) : w;
            int ch = h > w ? Math.Min(step, y + h - cy) : h;
            if (cw <= 0 || ch <= 0) { continue; }

            var saved = new Terraria.TileData[cw * ch];
            int k = 0;
            for (int i = 0; i < cw; i++)
            {
                for (int j = 0; j < ch; j++)
                {
                    int tx = cx + i, ty = cy + j;
                    if (tx < 1 || ty < 1 || tx >= ctx.Main.maxTilesX - 1 || ty >= ctx.Main.maxTilesY - 1) { saved[k++] = default; continue; }
                    saved[k++] = ctx.Main.tile[tx, ty];

                    ref var tile = ref ctx.Main.tile[tx, ty];
                    bool hadTile = tile.active();
                    if (!hadTile)
                    {
                        tile.ClearEverything();
                        tile.active(true);
                        tile.type = Terraria.ID.TileID.MagicalIceBlock;
                        tile.inActive(true);
                    }
                    try { tile.color(paint); } catch { }
                }
            }

            // ★ 还原必须在 finally 里：发包含异常时也不能把真实图格留在"假图格"状态（地图会被污损）
            try
            {
                player.SendTileRect((short)cx, (short)cy, (byte)Math.Min(cw, 255), (byte)Math.Min(ch, 255));
            }
            catch { }
            finally
            {
                k = 0;
                for (int i = 0; i < cw; i++)
                {
                    for (int j = 0; j < ch; j++)
                    {
                        int tx = cx + i, ty = cy + j;
                        var original = saved[k++];
                        if (tx < 1 || ty < 1 || tx >= ctx.Main.maxTilesX - 1 || ty >= ctx.Main.maxTilesY - 1) { continue; }
                        ctx.Main.tile[tx, ty] = original;
                    }
                }
            }
        }
    }
}