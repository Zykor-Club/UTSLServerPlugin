using System.Collections.Immutable;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace GenerateMap;

[PluginMetadata("GenerateMap", "2.2.0", "少司命; Cai; 千亦; 星梦（UTSL 移植 + 多世界拼接）", "生成多世界独立地图图片，并可按世界链横向拼接成一张大图")]
public sealed class GenerateMapPlugin : BasePlugin
{
    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock")
            {
                await initInfo.InitializationTask;
            }
        }

        // 输出目录挂在插件配置目录下，避免污染服务器根目录
        _ = configRegistrar.Directory;

        MapGenerator.Init();
        Commands.ChatCommands.Add(new Command("generatemap", Generate, "map", "生成地图", "generatemap"));
        Commands.ChatCommands.Add(new Command("generatemap", Stitch, "stitch", "缝合"));
        // ★ 分片出图驱动：UTSL 的 Game.PostUpdate 按世界触发，用它每 tick 推进一点渲染
        UnifierApi.EventHub.Game.PostUpdate.Register(RenderJob.OnPostUpdate, HandlerPriority.Normal);

        TShock.Log.Info("[GenerateMap] 已加载，指令：/map img 单世界 | /map all 全部世界 | /map 拼接 横向拼接大图 | /map 剖面 接缝剖面分析");

        // 诊断用：启动后自动输出一次接缝分析（受环境变量 GENERATEMAP_AUTOANALYZE 控制，
        // 不设则不跑）。这样无需玩家在线、也无需 REST 就能拿到数据。
        // 诊断用：启动后自动出图（受环境变量 GENERATEMAP_RENDER 控制）——
        // 让我自己就能看效果，不用每次都让玩家帮忙跑命令。
        if (Environment.GetEnvironmentVariable("GENERATEMAP_RENDER") == "1")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45));
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    List<ServerContext> worlds = [.. UnifiedServerCoordinator.Servers.Where(s => s.IsRunning)];
                    // ★ 按 EdgeStitch 的「世界链」顺序渲染，否则拼接图的世界顺序会乱
                    //   （协调器内部顺序不保证与世界链一致，实测会串位）。
                    List<string> chain = ReadChainOrder();
                    if (chain.Count > 0)
                    {
                        worlds = [.. worlds.OrderBy(w =>
                        {
                            int idx = chain.FindIndex(n => string.Equals(n, w.Name, StringComparison.OrdinalIgnoreCase));
                            return idx < 0 ? int.MaxValue : idx;
                        })];
                        TShock.Log.Info("[出图] 按世界链顺序渲染: " + string.Join(" — ", worlds.Select(w => w.Name)));
                    }
                    TShock.Log.Info($"[出图] 开始渲染 {worlds.Count} 个世界");

                    // ★ 分片渲染：有玩家的世界由 PostUpdate 每 tick 推进；没玩家的世界由看门狗同步渲完。
                    //   这样既不会长时间独占世界线程（卡服），空世界也照样能出图。
                    RenderJob.Start(worlds);
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[出图] 出错: " + ex);
                }
            });
        }

        // 诊断用：启动后自动执行一次"填陆"（受环境变量 GENERATEMAP_FILL 控制）
        if (Environment.GetEnvironmentVariable("GENERATEMAP_FILL") == "1")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(40));
                    TShock.Log.Info("[填陆] 自动填陆开始");
                    foreach (ServerContext world in UnifiedServerCoordinator.Servers)
                    {
                        if (!world.IsRunning)
                        {
                            continue;
                        }
                        ServerContext w = world;
                        RunOnWorld(w, () => SeamFiller.FillSeamLands(w, msg => TShock.Log.Info(msg)));
                    }
                    TShock.Log.Info("[填陆] 自动填陆结束（记得保存世界）");
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[填陆] 出错: " + ex);
                }
            });
        }

        // 诊断用：启动后自动执行一次"抹海"（受环境变量 GENERATEMAP_ERASE 控制）
        if (Environment.GetEnvironmentVariable("GENERATEMAP_ERASE") == "1")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(40));
                    TShock.Log.Info("[抹海] 自动抹海开始");
                    foreach (ServerContext world in UnifiedServerCoordinator.Servers)
                    {
                        if (!world.IsRunning)
                        {
                            continue;
                        }
                        ServerContext w = world;
                        RunOnWorld(w, () => SeamStitcher.EraseSeamOceans(w, msg => TShock.Log.Info(msg)));
                    }
                    TShock.Log.Info("[抹海] 自动抹海结束（记得保存世界）");
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[抹海] 出错: " + ex);
                }
            });
        }

        if (Environment.GetEnvironmentVariable("GENERATEMAP_AUTOANALYZE") == "1")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(40));
                    TShock.Log.Info("[分析] 自动分析开始（GENERATEMAP_AUTOANALYZE=1）");
                    foreach (ServerContext world in UnifiedServerCoordinator.Servers)
                    {
                        if (!world.IsRunning)
                        {
                            continue;
                        }
                        ServerContext w = world;
                        RunOnWorld(w, () => SeamAnalyzer.Dump(w, msg => TShock.Log.Info(msg)));
                    }
                    TShock.Log.Info("[分析] 自动分析结束");
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[分析] 自动分析出错: " + ex);
                }
            });
        }
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            Commands.ChatCommands.RemoveAll(x => x.CommandDelegate == Generate || x.CommandDelegate == Stitch);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>抹海缝合工具的命令入口。/stitch 分析 —— 先出数据。</summary>
    private static void Stitch(CommandArgs args)
    {
        TSPlayer? player = args.Player;

        if (args.Parameters.Count == 0 || !args.Parameters[0].Equals("分析", StringComparison.OrdinalIgnoreCase))
        {
            player?.SendSuccessMessage("缝合工具（抹海缝合，先分析后执行）：");
            player?.SendSuccessMessage("/stitch 分析 —— 量出各边缘的海洋范围、宝箱位置、内陆材质（写入服务器日志）");
            return;
        }

        List<ServerContext> worlds = [.. UnifiedServerCoordinator.Servers.Where(s => s.IsRunning)];
                    // ★ 按 EdgeStitch 的「世界链」顺序渲染，否则拼接图的世界顺序会乱
                    //   （协调器内部顺序不保证与世界链一致，实测会串位）。
                    List<string> chain = ReadChainOrder();
                    if (chain.Count > 0)
                    {
                        worlds = [.. worlds.OrderBy(w =>
                        {
                            int idx = chain.FindIndex(n => string.Equals(n, w.Name, StringComparison.OrdinalIgnoreCase));
                            return idx < 0 ? int.MaxValue : idx;
                        })];
                        TShock.Log.Info("[出图] 按世界链顺序渲染: " + string.Join(" — ", worlds.Select(w => w.Name)));
                    }
        player?.SendInfoMessage($"[缝合] 正在分析 {worlds.Count} 个世界的边缘，结果写入服务器日志…");

        foreach (ServerContext world in worlds)
        {
            ServerContext w = world;
            RunOnWorld(w, () => SeamAnalyzer.Dump(w, msg => TShock.Log.Info(msg)));
        }

        player?.SendSuccessMessage("[缝合] 分析完成，见日志中的 [分析] 行。");
    }

    private static void Generate(CommandArgs args)
    {
        TSPlayer? player = args.Player;
        if (args.Parameters.Count == 0)
        {
            ShowHelp(player);
            return;
        }

        switch (args.Parameters[0].ToLowerInvariant())
        {
            case "img":
            {
                ServerContext? current = player?.GetCurrentServer();
                if (current is null)
                {
                    player?.SendErrorMessage("[GenerateMap] 无法确定当前世界。");
                    return;
                }
                RenderOne(player, current);
                break;
            }
            case "all":
            {
                foreach (ServerContext world in UnifiedServerCoordinator.Servers)
                {
                    if (world.IsRunning)
                    {
                        RenderOne(player, world);
                    }
                }
                break;
            }
            case "拼接":
            case "stitch":
            {
                List<ServerContext> worlds = [.. UnifiedServerCoordinator.Servers.Where(s => s.IsRunning)];
                    // ★ 按 EdgeStitch 的「世界链」顺序渲染，否则拼接图的世界顺序会乱
                    //   （协调器内部顺序不保证与世界链一致，实测会串位）。
                    List<string> chain = ReadChainOrder();
                    if (chain.Count > 0)
                    {
                        worlds = [.. worlds.OrderBy(w =>
                        {
                            int idx = chain.FindIndex(n => string.Equals(n, w.Name, StringComparison.OrdinalIgnoreCase));
                            return idx < 0 ? int.MaxValue : idx;
                        })];
                        TShock.Log.Info("[出图] 按世界链顺序渲染: " + string.Join(" — ", worlds.Select(w => w.Name)));
                    }
                if (worlds.Count == 0)
                {
                    player?.SendErrorMessage("[GenerateMap] 没有运行中的世界。");
                    return;
                }

                string fileName = $"stitched_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                player?.SendInfoMessage($"[GenerateMap] 正在拼接 {worlds.Count} 个世界，请稍候…");
                RenderAllAndStitch(player, worlds, fileName);
                break;
            }
            case "剖面":
            case "profile":
            {
                List<ServerContext> worlds = [.. UnifiedServerCoordinator.Servers.Where(s => s.IsRunning)];
                    // ★ 按 EdgeStitch 的「世界链」顺序渲染，否则拼接图的世界顺序会乱
                    //   （协调器内部顺序不保证与世界链一致，实测会串位）。
                    List<string> chain = ReadChainOrder();
                    if (chain.Count > 0)
                    {
                        worlds = [.. worlds.OrderBy(w =>
                        {
                            int idx = chain.FindIndex(n => string.Equals(n, w.Name, StringComparison.OrdinalIgnoreCase));
                            return idx < 0 ? int.MaxValue : idx;
                        })];
                        TShock.Log.Info("[出图] 按世界链顺序渲染: " + string.Join(" — ", worlds.Select(w => w.Name)));
                    }
                player?.SendInfoMessage($"[GenerateMap] 正在分析 {worlds.Count} 个世界的接缝剖面，结果写入服务器日志…");
                foreach (ServerContext world in worlds)
                {
                    ServerContext w = world;
                    RunOnWorld(w, () => SeamProfile.Dump(w, msg => TShock.Log.Info(msg)));
                }
                player?.SendSuccessMessage("[GenerateMap] 剖面分析完成，见服务器日志中的 [剖面] 行。");
                break;
            }
            default:
                ShowHelp(player);
                break;
        }
    }

    /// <summary>
    /// 把渲染动作安全地放到目标世界上执行。
    ///
    /// ⚠️ 关键：UTSL 里【没有玩家在线的世界不会 tick】，此时 world.Dispatcher.Post() 排进去的动作
    /// 永远不会被执行（实测 /map all 与 /map 拼接 因此一张图都出不来，拼接链卡在第一个空闲世界）。
    /// 对这类空闲世界，没有别的线程在改动它的状态，直接从当前线程读是安全的。
    /// </summary>
    /// <summary>从 EdgeStitch 配置读「世界链」；读不到返回空表（表示按协调器原顺序）。</summary>
    private static List<string> ReadChainOrder()
    {
        try
        {
            string path = Path.Combine("config", "EdgeStitch", "EdgeStitch.json");
            if (!File.Exists(path)) { return []; }
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("世界链", out System.Text.Json.JsonElement e) && e.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                List<string> list = [];
                foreach (System.Text.Json.JsonElement it in e.EnumerateArray())
                {
                    if (it.GetString() is string s) { list.Add(s); }
                }
                return list;
            }
        }
        catch { }
        return [];
    }

    /// <summary>从 EdgeStitch 配置读「切片间距_格」；读不到返回 0（普通拼接）。</summary>
    private static int ReadSliceDelta()
    {
        try
        {
            string path = Path.Combine(TShock.SavePath, "..", "config", "EdgeStitch", "EdgeStitch.json");
            if (!File.Exists(path))
            {
                path = Path.Combine("config", "EdgeStitch", "EdgeStitch.json");
            }
            if (!File.Exists(path))
            {
                return 0;
            }
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("切片间距_格", out System.Text.Json.JsonElement e) && e.TryGetInt32(out int d) && d > 0)
            {
                return d;
            }
        }
        catch
        {
        }
        return 0;
    }

    private static void RunOnWorld(ServerContext world, Action action)
    {
        if (world.NPC.GetActivePlayerCount() > 0)
        {
            world.Dispatcher.Post(action);
        }
        else
        {
            action();
        }
    }

    private static void RenderOne(TSPlayer? player, ServerContext world)
    {
        string fileName = $"{world.Name}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
        player?.SendInfoMessage($"[GenerateMap] 正在渲染世界 {world.Name} …");

        // 有人在线时在该世界自己的线程上渲染；空闲世界直接渲染（见 RunOnWorld 注释）
        RunOnWorld(world, () =>
        {
            try
            {
                string path = MapGenerator.SaveWorldImg(world, fileName);
                player?.SendSuccessMessage($"[GenerateMap] {world.Name} 地图已保存: {path}");
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[GenerateMap] 渲染出错: " + ex);
            }
        });
    }

    /// <summary>
    /// 逐世界串行渲染再横向拼接。
    ///
    /// 为什么要串行：地图渲染要用 Terraria 的全局地图对象（Main.Map），而每次渲染前会先用
    /// 当前世界的图格重建它 —— 如果多个世界的渲染并发跑在不同线程上，会互相覆盖。
    /// 所以这里让每个世界渲染完后，才推进到下一个世界（各自仍在自己的线程上执行）。
    /// </summary>
    private static void RenderAllAndStitch(TSPlayer? player, List<ServerContext> worlds, string fileName)
    {
        List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>> images = [];
        int index = 0;

        void Step()
        {
            if (index >= worlds.Count)
            {
                try
                {
                    string path = MapGenerator.SaveStitchedImg(images, fileName, drawSeamLine: true, sliceDelta: ReadSliceDelta());   // ★ 命令路径也必须传 Δ，否则拼接线会错位 300 列
                    player?.SendSuccessMessage($"[GenerateMap] 拼接图已保存: {path}（共 {images.Count} 个世界）");
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[GenerateMap] 拼接出错: " + ex);
                }
                return;
            }

            ServerContext world = worlds[index++];
            RunOnWorld(world, () =>
            {
                try
                {
                    images.Add(MapGenerator.RenderWorld(world));
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[GenerateMap] 渲染 " + world.Name + " 出错: " + ex);
                }
                Step();
            });
        }

        Step();
    }

    private static void ShowHelp(TSPlayer? player)
    {
        player?.SendSuccessMessage("GenerateMap 帮助：");
        player?.SendSuccessMessage("/map img   —— 渲染当前世界为 PNG");
        player?.SendSuccessMessage("/map all   —— 渲染所有运行中的世界");
        player?.SendSuccessMessage("/map 拼接  —— 把所有世界按顺序横向拼接成一张大图（验收接缝用）");
        player?.SendSuccessMessage("/map 剖面  —— 输出各世界边缘的地表高度与方块分布（设计缝合用）");
    }
}
