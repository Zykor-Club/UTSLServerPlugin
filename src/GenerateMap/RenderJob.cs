using System.Text.Json;
using SixLabors.ImageSharp.PixelFormats;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Servers;

namespace GenerateMap;

/// <summary>
/// 分片出图任务 —— 避免长时间独占世界线程造成卡服（实测同步渲染 3 个世界 4.3 秒）。
///
/// 驱动方式（两条腿走路）：
///   ① 有玩家的世界：挂在 Game.PostUpdate 上，每 tick 渲 80 行（玩家几乎无感）。
///   ② 没玩家的世界：UTSL 里空世界【不 tick】→ PostUpdate 永远不来 →
///      由【看门狗】在后台线程直接同步渲完（此时该世界是静止的，没有并发写入，安全）。
/// </summary>
internal static class RenderJob
{
    private const int RowsPerTick = 80;
    private const long WatchdogIdleMs = 2500;   // 超过这么久没推进，就认为该世界没在 tick

    private static Queue<ServerContext> queue = new();
    private static List<SixLabors.ImageSharp.Image<Rgba32>> images = [];
    private static MapGenerator.ChunkRender? current;
    private static System.Diagnostics.Stopwatch? timer;
    private static long lastAdvance;
    private static bool running;

    public static bool IsActive => current is not null || queue.Count > 0;

    /// <summary>开始一次分片出图（在任意线程调用）。</summary>
    public static void Start(IEnumerable<ServerContext> worlds)
    {
        queue = new Queue<ServerContext>(worlds);
        images = [];
        current = null;
        timer = System.Diagnostics.Stopwatch.StartNew();
        lastAdvance = Environment.TickCount64;

        if (!running)
        {
            running = true;
            _ = Task.Run(WatchdogLoop);
        }
    }

    /// <summary>世界 tick 回调（args.Content.Server 是本次 tick 的世界）。</summary>
    public static void OnPostUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        if (!IsActive) { return; }
        ServerContext server = args.Content.Server;
        lastAdvance = Environment.TickCount64;

        if (current is null)
        {
            if (queue.Count == 0) { return; }
            current = MapGenerator.BeginChunkedRender(server);
        }
        if (!SameWorld(current.Server, server)) { return; }

        if (MapGenerator.StepChunkedRender(current, RowsPerTick))
        {
            CompleteCurrent();
        }
    }

    /// <summary>看门狗：处理"没有玩家、世界不 tick"的那部分世界。</summary>
    private static async Task WatchdogLoop()
    {
        while (true)
        {
            await Task.Delay(700).ConfigureAwait(false);
            try
            {
                if (!IsActive) { continue; }
                if (Environment.TickCount64 - lastAdvance < WatchdogIdleMs) { continue; }   // 有人在推进，让 PostUpdate 走

                // 该世界没在 tick（空世界）→ 世界状态静止，在本线程渲完是安全的
                current ??= MapGenerator.BeginChunkedRender(queue.Peek());
                while (!MapGenerator.StepChunkedRender(current, 400)) { }
                CompleteCurrent();
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[出图] 看门狗渲染失败: " + ex);
                current = null;
                if (queue.Count > 0) { queue.Dequeue(); }
                images = [];
                timer = null;
            }
        }
    }

    /// <summary>当前世界渲染完成：收集图片，全部完成后拼接保存。</summary>
    private static void CompleteCurrent()
    {
        if (current is null) { return; }
        images.Add(current.Image);
        TShock.Log.Info("[出图] 已渲染 " + current.Server.Name);
        current = null;
        if (queue.Count > 0) { queue.Dequeue(); }
        lastAdvance = Environment.TickCount64;

        if (queue.Count == 0)
        {
            int sliceDelta = ReadSliceDelta();
            string path = MapGenerator.SaveStitchedImg(images, $"auto_stitched_{DateTime.Now:HH-mm-ss}.png", drawSeamLine: true, sliceDelta: sliceDelta);
            TShock.Log.Info($"[出图] 拼接图已保存: {path}（总耗时 {timer?.Elapsed.TotalSeconds:F1}s，分片渲染）");
            images = [];
            timer = null;
        }
    }

    private static bool SameWorld(ServerContext a, ServerContext b)
        => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>从 EdgeStitch 配置读「切片间距_格」（与插件里同名方法一致）。</summary>
    private static int ReadSliceDelta()
    {
        try
        {
            string path = Path.Combine("config", "EdgeStitch", "EdgeStitch.json");
            if (!File.Exists(path)) { return 0; }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("切片间距_格", out JsonElement e) && e.TryGetInt32(out int v)) { return v; }
        }
        catch { }
        return 0;
    }
}
