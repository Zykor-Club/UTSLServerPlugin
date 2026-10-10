using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Terraria.Map;
using UnifierTSL.Servers;

// 注意：SixLabors.ImageSharp.Color 与 Microsoft.Xna.Framework.Color 同名，
// 本文件里所有像素/颜色类型一律用【完全限定名】，避免歧义。
using Rgba32 = SixLabors.ImageSharp.PixelFormats.Rgba32;
using ImageRgba = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace GenerateMap;

/// <summary>
/// 把世界渲染成 PNG。
///
/// 与 TShock 原版（少司命/Cai/千亦）的差异：
///   1) 每世界状态从 server.* 取；地图对象是 Terraria 的全局 Main.Map（每次渲染前用当前世界的图格重建）
///   2) 【必须】在世界线程上渲染：原版用 Task.Run 丢后台线程，UTSL 下读图格属于访问该世界状态，
///      跨线程不安全 —— 调用方统一用 server.Dispatcher.Post(...) 包住
///   3) 去掉了 .map 文件功能（依赖客户端玩家路径），只保留图片渲染
/// </summary>
public static class MapGenerator
{
    private const string BasePath = "GenerateMap";
    public static readonly string ImagesPath = Path.Combine(BasePath, "Images");
    private const int Edge = WorldMap.BlackEdgeWidth;

    public static void Init()
    {
        MapHelper.Initialize();
        Directory.CreateDirectory(ImagesPath);
    }

    private static WorldMap CreateWorkingMap(ServerContext server)
        => new(server.Main.maxTilesX, server.Main.maxTilesY)
        {
            _tiles = new MapTile[server.Main.maxTilesX + (Edge * 2), server.Main.maxTilesY + (Edge * 2)],
        };

    /// <summary>分片渲染状态（跨 tick 用）。</summary>
    public sealed class ChunkRender
    {
        public ServerContext Server = null!;
        public ImageRgba Image = null!;
        public int MapRow;
        public int PixelRow;
        public bool MapDone;
    }

    /// <summary>开始一次分片渲染（必须在目标世界的线程上调用一次）。</summary>
    public static ChunkRender BeginChunkedRender(ServerContext server)
    {
        Terraria.Main.Map = CreateWorkingMap(server);
        return new ChunkRender { Server = server, Image = new ImageRgba(server.Main.maxTilesX, server.Main.maxTilesY) };
    }

    /// <summary>
    /// 每 tick 推进 rows 行。返回 true 表示整张图已完成。
    /// 分两个阶段：先按行建地图（CreateMapTile，最贵），再按行写像素。
    /// </summary>
    public static bool StepChunkedRender(ChunkRender st, int rows)
    {
        ServerContext server = st.Server;
        int maxX = server.Main.maxTilesX;
        int maxY = server.Main.maxTilesY;

        if (!st.MapDone)
        {
            int end = Math.Min(st.MapRow + rows, maxY);
            int width = Terraria.Main.Map._tiles.GetLength(0);
            int height = Terraria.Main.Map._tiles.GetLength(1);
            for (int y = st.MapRow; y < end; y++)
            {
                for (int x = 0; x < maxX; x++)
                {
                    MapTile tile;
                    try { tile = server.MapHelper.CreateMapTile(x, y, byte.MaxValue); }
                    catch (Exception) { continue; }
                    if ((uint)x < (uint)width && (uint)y < (uint)height) { Terraria.Main.Map._tiles[x, y] = tile; }
                    int rawX = x + Edge, rawY = y + Edge;
                    if ((uint)rawX < (uint)width && (uint)rawY < (uint)height) { Terraria.Main.Map._tiles[rawX, rawY] = tile; }
                }
            }
            st.MapRow = end;
            if (st.MapRow >= maxY) { st.MapDone = true; }
            return false;
        }

        int pend = Math.Min(st.PixelRow + rows, maxY);
        for (int y = st.PixelRow; y < pend; y++)
        {
            for (int x = 0; x < maxX; x++)
            {
                MapTile tile = Terraria.Main.Map._tiles[x + Edge, y + Edge];
                XnaColor col = MapHelper.GetMapTileXnaColor(tile, x + Edge, y + Edge);
                st.Image[x, y] = new Rgba32(col.R, col.G, col.B, col.A);
            }
        }
        st.PixelRow = pend;
        return st.PixelRow >= maxY;
    }

    /// <summary>一次同步渲染整个世界（手动 /map 用）。</summary>
    public static ImageRgba RenderWorld(ServerContext server)
    {
        ChunkRender st = BeginChunkedRender(server);
        int rows = Math.Max(1, server.Main.maxTilesY / 1);
        while (!StepChunkedRender(st, rows)) { }
        return st.Image;
    }

    public static string SaveWorldImg(ServerContext server, string fileName)
    {
        using ImageRgba image = RenderWorld(server);
        string path = Path.Combine(ImagesPath, fileName);
        image.SaveAsPng(path);
        return path;
    }

    /// <summary>
    /// 按给定顺序把多个世界【横向拼接】成一张大图 —— 多世界拼接的验收工具：
    /// 接缝在哪、两侧地形/群系是否连续，一眼可见。
    /// </summary>
    public static string SaveStitchedImg(IReadOnlyList<ServerContext> worlds, string fileName, bool drawSeamLine = true)
    {
        List<ImageRgba> images = [];
        foreach (ServerContext world in worlds)
        {
            images.Add(RenderWorld(world));
        }
        return SaveStitchedImg(images, fileName, drawSeamLine);
    }

    /// <summary>
    /// 按已渲染好的图片横向拼接。调用方负责先在各自世界线程上渲染好。
    ///
    /// sliceDelta &gt; 0 时按【切片模式】拼接：第 i 个世界只取它自己的 [0, sliceDelta)，
    /// 因为第 i 片的原点在大地图上是 i*sliceDelta。
    /// 这样拼出来才是玩家实际体验的连续地图（否则并排时坐标会差 i*sliceDelta）。
    /// </summary>
    public static string SaveStitchedImg(IReadOnlyList<ImageRgba> rendered, string fileName, bool drawSeamLine = true, int sliceDelta = 0)
    {
        List<ImageRgba> images = [.. rendered];
        int maxHeight = images.Count == 0 ? 1 : images.Max(i => i.Height);
        // 切片模式：前 N-1 片各取 [0, Δ)，最后一片取 [0, 宽-42)
        //   （相邻片相差 Δ；最后一片要展示到地图末端，但排除世界最右那条不可达边缘带 ——
        //     那 42 列玩家永远走不到、也永远没被点亮，画进去就是一条“假地形条”，还会切掉右侧海洋的尾巴）
        const int UnreachableEdge = 42;
        int totalWidth = sliceDelta > 0
            ? sliceDelta * (images.Count - 1) + Math.Max(1, images[^1].Width - UnreachableEdge)
            : images.Sum(i => i.Width);

        using ImageRgba canvas = new(Math.Max(1, totalWidth), Math.Max(1, maxHeight));

        int offset = 0;
        for (int i = 0; i < images.Count; i++)
        {
            ImageRgba img = images[i];
            int x = offset;
            if (sliceDelta > 0)
            {
                bool isLast = i == images.Count - 1;
                int wantW = isLast ? Math.Max(1, img.Width - UnreachableEdge) : sliceDelta;
                if (img.Width <= wantW)
                {
                    canvas.Mutate(ctx => ctx.DrawImage(img, new Point(x, 0), 1f));
                    offset += wantW;
                    img.Dispose();
                    continue;
                }
                int drawW = Math.Min(wantW, canvas.Width - x);
                if (drawW > 0)
                {
                    using ImageRgba part = img.Clone(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(0, 0, drawW, img.Height)));
                    canvas.Mutate(ctx => ctx.DrawImage(part, new Point(x, 0), 1f));
                }
                offset += wantW;
                img.Dispose();
                continue;
            }
            canvas.Mutate(ctx => ctx.DrawImage(img, new Point(x, 0), 1f));

            if (drawSeamLine && i > 0)
            {
                // 直接写像素画接缝线，不依赖任何绘制扩展方法（避免 API 签名差异）
                int lineX = Math.Clamp(x, 0, canvas.Width - 1);
                for (int yy = 0; yy < canvas.Height; yy++)
                {
                    canvas[lineX, yy] = new Rgba32(255, 0, 0, 255);
                }
            }

            offset += img.Width;
            img.Dispose();
        }

        string path = Path.Combine(ImagesPath, fileName);
        canvas.SaveAsPng(path);
        return path;
    }
}
