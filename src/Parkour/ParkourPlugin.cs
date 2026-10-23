using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TShockAPI;
using UnifierTSL.Plugins;
using UnifierTSL.PluginService;
using UTSL.MiniGame;

namespace Parkour;

/// <summary>
/// 跑酷子插件。只依赖 <c>MiniGameCore.Abstractions</c>：
///   /pk solo &lt;map&gt;          开始单人跑（房间世界 = 空白世界）
///   /pk build [map]          进入建造世界（可编辑，圈区域用）
///   /pk here start|finish|save &lt;n&gt;|activity [半径]   以当前位置圈区域并保存
///   /pk show                 列出当前地图的所有区域
///   /pk maps                 列出地图
///   /pk leave                离开房间/建造世界
/// </summary>
[PluginMetadata("Parkour", "0.1.0", "星梦",
    "跑酷小游戏：空白世界 + 玩家自定义起点/终点/存档点/活动区（依赖 MiniGameCore）")]
public sealed class ParkourPlugin : BasePlugin
{
    private static IMiniGameService? service;
    private static ParkourGame? game;
    private static bool apiMismatch;
    private static string discoverError = "";
    private static ParkourConfig config = new();
    private static System.Threading.Timer? paintTimer;

    /// <summary>两角选点（照领地插件 /region set 1|2 的手感）：p1/p2，优先于半径。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int X, int Y)> Pos1 = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int X, int Y)> Pos2 = new();

    public override void BeforeGlobalInitialize(ImmutableArray<IPluginContainer> plugins)
    {
        try
        {
            foreach (IPluginContainer c in plugins)
            {
                if (c.Plugin is not IMiniGameServiceProvider provider) { continue; }
                apiMismatch = provider.ApiVersion != MiniGameApi.Version;
                service = apiMismatch ? null : provider.Api;
                break;
            }
        }
        catch (Exception ex)
        {
            service = null;
            discoverError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    public override Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        string dir = "";
        try { dir = configRegistrar.Directory; } catch { }
        config = ParkourConfig.Load(string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "settings.json"));

        try { ParkourMapStore.Init(dir); } catch (Exception ex) { TShock.Log.Error("[跑酷] 初始化地图目录失败: " + ex.Message); }

        if (service is not null)
        {
            game = new ParkourGame(config);
            service.RegisterGame(game);
        }

        Commands.ChatCommands.Add(new Command("parkour.play", PkCommand, "pk"));

        // 区域可视化：默认开（配置项），定时给跑酷世界里的玩家画边界（只对他自己可见）
        if (config.RegionDisplay)
        {
            int period = Math.Max(1, config.RegionDisplayRefreshSeconds) * 1000;
            paintTimer = new System.Threading.Timer(_ => PaintAll(), null, 3000, period);
            TShock.Log.Info($"[跑酷] 区域显示已开启（每 {period / 1000}s 刷新，范围 {config.RegionDisplayRange} 格）");
        }

        TShock.Log.Info(service is not null
            ? $"[跑酷] 已加载 v0.1.0（契约 v{MiniGameApi.Version}；地图目录 {ParkourMapStore.Root}）"
            : apiMismatch
                ? $"[跑酷] ★契约版本不匹配（期望 v{MiniGameApi.Version}），玩法已禁用"
                : $"[跑酷] ★未取得 MiniGameCore，玩法已禁用{(discoverError.Length > 0 ? "（" + discoverError + "）" : "")}");
        return Task.CompletedTask;
    }

    /// <summary>给跑酷房间里的每个玩家画一遍区域边界。</summary>
    private static void PaintAll()
    {
        try
        {
            if (service is null || !config.RegionDisplay) { return; }
            int range = Math.Clamp(config.RegionDisplayRange, 16, 400);

            foreach (IMiniGameRoom room in service.Rooms)
            {
              try
              {
                if (room.GameId != "parkour") { continue; }
                if (room.Map is not ParkourMap map) { continue; }
                if (room.World?.Context is not UnifierTSL.Servers.ServerContext ctx) { continue; }

                foreach (int idx in room.Players)
                {
                    TSPlayer? p = TShock.Players[idx];
                    if (p is not { Active: true }) { continue; }
                    // 必须确认这人此刻真在这个房间世界里，否则会把别的世界的坐标发给他
                    if (!ReferenceEquals(UnifierTSL.UnifiedServerCoordinator.GetClientCurrentlyServer(idx), ctx)) { continue; }

                    if (map.Activity.Length == 4) { RegionPainter.Draw(ctx, p, map.ActivityRect, 25, range); }
                    if (map.HasStart) { RegionPainter.Draw(ctx, p, map.StartRect, 5, range); }
                    if (map.HasFinish) { RegionPainter.Draw(ctx, p, map.FinishRect, 13, range); }
                    List<SavePoint> saves = map.Saves;       // 快照引用（写侧是 copy-on-write）
                    foreach (SavePoint sp in saves) { RegionPainter.Draw(ctx, p, sp.Rect, 14, range); }
                }
              }
              catch (Exception ex) { try { TShock.Log.Warning("[跑酷] 区域显示失败: " + ex.Message); } catch { } }
            }
        }
        catch (Exception ex) { try { TShock.Log.Warning("[跑酷] 区域显示失败: " + ex.Message); } catch { } }
    }

    private void PkCommand(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "help";
        TSPlayer? p = args.Player;

        if (sub is "help" or "")
        {
            p?.SendInfoMessage("用法: /pk p1 | /pk p2（两角选点）→ /pk here start|spawn|finish|save <n>|activity（优先用选区，没选区才用 ±半径）| /pk sel | /pk clear <区域> | /pk set <countdown|time|grace> <值> | /pk solo <图> | /pk build [图] | /pk show | /pk maps | /pk prep | /pk leave");
            return;
        }

        if (service is null)
        {
            p?.SendErrorMessage(apiMismatch ? "小游戏核心契约版本不匹配" : "小游戏核心未加载");
            return;
        }

        switch (sub)
        {
            case "maps":
            {
                if (game is null) { p?.SendErrorMessage("跑酷玩法未注册"); return; }
                foreach (IMapDefinition m in game.Maps)
                {
                    if (m is not ParkourMap pm) { continue; }
                    string size = pm.TilesX > 0 ? $"{pm.TilesX}x{pm.TilesY}" : "跟随当前世界";
                    string areas = $"{(pm.HasStart ? "起点✓" : "起点✗")} {(pm.HasFinish ? "终点✓" : "终点✗")} 存档{pm.Saves.Count}个 {(pm.Activity.Length == 4 ? "活动区✓" : "活动区✗")}";
                    p?.SendInfoMessage($"  {pm.Id} · {pm.DisplayName} · {size} · {areas}");
                }
                return;
            }

            case "build":
            {
                string mapId = args.Parameters.Count > 1 ? args.Parameters[1] : "test";
                p?.SendInfoMessage("[跑酷] 进入建造世界…");
                _ = RunAsync(p, () => service.JoinBuildAsync(p!, "parkour", mapId, CancellationToken.None));
                return;
            }

            case "solo":
            {
                string mapId = args.Parameters.Count > 1 ? args.Parameters[1] : "test";
                if (game?.FindMap(mapId) is ParkourMap checkMap)
                {
                    List<string> problems = checkMap.Validate();
                    if (problems.Count > 0)
                    {
                        p?.SendErrorMessage("[跑酷] 这张图还不能开跑：");
                        foreach (string msg in problems) { p?.SendErrorMessage("  · " + msg); }
                        return;
                    }
                }
                p?.SendInfoMessage("[跑酷] 准备中…");
                _ = RunAsync(p, () => service.JoinSoloAsync(p!, "parkour", mapId, CancellationToken.None));
                return;
            }

            case "newmap":
            {
                if (p is null) { return; }
                if (!p.HasPermission("parkour.admin")) { p.SendErrorMessage("需要 parkour.admin 权限"); return; }
                if (args.Parameters.Count < 3)
                {
                    p.SendErrorMessage("用法: /pk newmap <图id> <世界文件名(不含.wld)> [显示名]");
                    return;
                }
                if (game is null) { p.SendErrorMessage("跑酷玩法未注册"); return; }

                string id = args.Parameters[1].ToLowerInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9_-]{1,64}$"))
                {
                    p.SendErrorMessage("图 id 只能用小写字母/数字/_/-（1~64 位）");
                    return;
                }
                string wld = args.Parameters[2].EndsWith(".wld", StringComparison.OrdinalIgnoreCase)
                    ? args.Parameters[2][..^4] : args.Parameters[2];
                string path = Path.Combine(Terraria.Main.WorldPath, wld + ".wld");
                if (!File.Exists(path)) { p.SendErrorMessage($"找不到世界文件: {path}"); return; }

                string disp = args.Parameters.Count > 3 ? string.Join(" ", args.Parameters.Skip(3)) : id;
                ParkourMap created = ParkourMap.CreateBlank(id, disp, 0, 0);
                created.WorldFile = wld;

                if (!ParkourMapStore.Save(created, out string saved)) { p.SendErrorMessage("保存失败（看日志）"); return; }

                game.ReloadMaps();
                p.SendSuccessMessage($"[跑酷] 已建图 {id} → 世界 {wld}.wld（{saved}）");
                p.SendInfoMessage("下一步: /pk build " + id + " → 圈活动区 → 圈起点/终点 → /pk solo " + id);
                return;
            }

            case "prep":
            {
                // 预热空白基线：把"复制+清空"这次慢动作提前做掉，之后 /pk build 就直接复制基线
                if (p is null) { return; }
                if (!p.HasPermission("parkour.admin")) { p.SendErrorMessage("需要 parkour.admin 权限"); return; }
                p.SendInfoMessage("[跑酷] 预热中…");
                _ = Task.Run(async () =>
                {
                    bool ok = await service.PrepareWorldAsync(p, "parkour", "test", CancellationToken.None).ConfigureAwait(false);
                    p.SendInfoMessage(ok ? "[跑酷] 已就绪" : "[跑酷] 预热失败");
                });
                return;
            }

            case "leave":
                if (p is not null) { _ = service.LeaveAsync(p, CancellationToken.None); }
                return;

            case "p1":
            case "p2":
            {
                if (p is null) { return; }
                (int X, int Y) pos = (p.TileX, p.TileY);
                if (sub == "p1") { Pos1[p.Index] = pos; } else { Pos2[p.Index] = pos; }
                p.SendSuccessMessage($"[跑酷] {sub} = ({pos.X},{pos.Y})");
                if (Pos1.TryGetValue(p.Index, out var a) && Pos2.TryGetValue(p.Index, out var b))
                {
                    TileRect r = Normalize(a, b);
                    p.SendInfoMessage($"[跑酷] 当前选区 = {r}（{r.Width}x{r.Height}），可用 /pk here <区域> 套用");
                }
                return;
            }

            case "sel":
            {
                if (p is null) { return; }
                if (args.Parameters.Count > 1 && args.Parameters[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    Pos1.TryRemove(p.Index, out _); Pos2.TryRemove(p.Index, out _);
                    p.SendInfoMessage("[跑酷] 已清空选区");
                    return;
                }
                bool has1 = Pos1.TryGetValue(p.Index, out var a1);
                bool has2 = Pos2.TryGetValue(p.Index, out var b1);
                p.SendInfoMessage($"[跑酷] p1={(has1 ? $"({a1.X},{a1.Y})" : "未设")} p2={(has2 ? $"({b1.X},{b1.Y})" : "未设")}");
                if (has1 && has2)
                {
                    TileRect r = Normalize(a1, b1);
                    p.SendInfoMessage($"[跑酷] 选区 = {r}（{r.Width}x{r.Height} 格）");
                }
                else { p.SendInfoMessage("[跑酷] 用法: 站到一角 /pk p1，站到对角 /pk p2"); }
                return;
            }

            case "clear":
                ClearCommand(args);
                return;

            case "set":
                SetCommand(args);
                return;

            case "here":
                HereCommand(args);
                return;

            case "show":
                ShowCommand(args);
                return;

            case "save":
            {
                IMiniGameRoom? room = p is null ? null : service.FindByPlayer(p.Index);
                if (room?.Map is not ParkourMap pm) { p?.SendErrorMessage("你不在跑酷房间里"); return; }
                p?.SendInfoMessage(ParkourMapStore.Save(pm, out string path) ? $"[跑酷] 已保存 {pm.Id} → {path}" : "[跑酷] 保存失败（看日志）");
                game?.ReloadMaps();
                return;
            }

            default:
                p?.SendInfoMessage("用法: /pk solo <图> | /pk build [图] | /pk here ... | /pk show | /pk maps | /pk leave");
                return;
        }
    }

    /// <summary>以玩家当前位置为心圈一块区域（优先用 p1/p2 选区，否则 ±半径）。</summary>
    private void HereCommand(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }
        if (!p.HasPermission("parkour.admin")) { p.SendErrorMessage("需要 parkour.admin 权限"); return; }

        IMiniGameRoom? room = service!.FindByPlayer(p.Index);
        if (room?.Map is not ParkourMap map)
        {
            p.SendErrorMessage("你不在跑酷世界里（先 /pk build <图>）");
            return;
        }
        if (args.Parameters.Count < 2) { p.SendErrorMessage("用法: /pk here start|spawn|finish|save <序号>|activity [半径]"); return; }

        string kind = args.Parameters[1].ToLowerInvariant();
        int x = p.TileX, y = p.TileY;

        if (kind != "activity" && map.Activity.Length != 4)
        {
            p.SendErrorMessage("还没圈活动区：先 /pk here activity，再圈起点/终点/存档点");
            return;
        }

        int Radius(int argIndex, int fallback)
            => args.Parameters.Count > argIndex && int.TryParse(args.Parameters[argIndex], out int v) && v > 0 && v < 20000 ? v : fallback;

        // 先算出矩形，再校验，最后才赋值 —— 这样"被拒绝"不会改动内存，也不需要回滚
        string source = "半径";
        TileRect Pick(int fallbackRadius)
        {
            if (Pos1.TryGetValue(p.Index, out var a) && Pos2.TryGetValue(p.Index, out var b))
            {
                source = "选区";
                return Normalize(a, b);
            }
            return TileRect.Around(x, y, fallbackRadius);
        }

        TileRect rect;
        int saveIndex = 0;

        switch (kind)
        {
            case "activity":
                rect = Pick(Radius(2, 32));
                break;

            case "start":
            case "finish":
                rect = Pick(Radius(2, 6));
                break;

            case "spawn":
                rect = default;
                break;

            case "save":
                if (args.Parameters.Count < 3 || !int.TryParse(args.Parameters[2], out saveIndex) || saveIndex <= 0)
                {
                    p.SendErrorMessage("用法: /pk here save <序号> [半径]（序号从 1 开始）");
                    return;
                }
                rect = Pick(Radius(3, 6));
                break;

            default:
                p.SendErrorMessage("类型只能是 start / spawn / finish / save / activity");
                return;
        }

        // ===== 硬校验（全部在赋值之前）=====
        if (kind != "activity")
        {
            TileRect act = map.ActivityRect;
            if (!ParkourMap.Inside(act, rect))
            {
                p.SendErrorMessage($"{rect} 超出了活动区 {act}，未保存。请先放大活动区。");
                return;
            }
        }
        if (kind is "start" or "finish" or "save" && (rect.Width < 3 || rect.Height < 3))
        {
            p.SendErrorMessage($"区域太小（{rect.Width}x{rect.Height}），至少要 3x3。用 /pk p1 /pk p2 圈大一点。");
            return;
        }

        // ===== 赋值 =====
        switch (kind)
        {
            case "activity":
                map.Activity = ParkourMap.FromRect(rect);
                p.SendSuccessMessage($"[跑酷] 活动区 = {rect}（{source}）");
                break;

            case "start":
            {
                int cxs = (rect.X0 + rect.X1) / 2, cys = (rect.Y0 + rect.Y1) / 2;
                map.Start = ParkourMap.FromRect(rect);
                map.StartSpawn = new[] { cxs, cys };
                p.SendSuccessMessage($"[跑酷] 起点 = {rect}（{source}），出生点取中心 ({cxs},{cys})");
                break;
            }

            case "spawn":
                map.StartSpawn = new[] { x, y };
                p.SendSuccessMessage($"[跑酷] 起点出生点 = ({x},{y})");
                break;

            case "finish":
                map.Finish = ParkourMap.FromRect(rect);
                p.SendSuccessMessage($"[跑酷] 终点 = {rect}（{source}）");
                break;

            case "save":
            {
                // copy-on-write：别的房间可能正在枚举 Saves，不能就地改
                List<SavePoint> next = map.Saves.Where(sp => sp.Index != saveIndex).ToList();
                next.Add(new SavePoint
                {
                    Index = saveIndex,
                    X0 = rect.X0, Y0 = rect.Y0, X1 = rect.X1, Y1 = rect.Y1,
                    SpawnX = (rect.X0 + rect.X1) / 2, SpawnY = (rect.Y0 + rect.Y1) / 2,
                });
                next.Sort((a, b) => a.Index.CompareTo(b.Index));
                map.Saves = next;
                p.SendSuccessMessage($"[跑酷] 存档点 {saveIndex} = {rect}（{source}）");
                break;
            }
        }

        p.SendInfoMessage(ParkourMapStore.Save(map, out string path) ? $"[跑酷] 已保存" : "[跑酷] 保存失败");
        game?.ReloadMaps();
    }
    private static TileRect Normalize((int X, int Y) a, (int X, int Y) b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private void ShowCommand(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }

        IMiniGameRoom? room = service!.FindByPlayer(p.Index);
        if (room?.Map is not ParkourMap map) { p.SendErrorMessage("你不在跑酷世界里"); return; }

        p.SendInfoMessage($"[跑酷] {map.Id}（{map.DisplayName}）");
        p.SendInfoMessage($"  活动区: {(map.Activity.Length == 4 ? map.ActivityRect.ToString() : "未设置")}");
        p.SendInfoMessage($"  起点  : {(map.HasStart ? $"{map.StartRect} 出生({(map.StartSpawn.Length == 2 ? map.StartSpawn[0] : 0)},{(map.StartSpawn.Length == 2 ? map.StartSpawn[1] : 0)})" : "未设置")}");
        p.SendInfoMessage($"  终点  : {(map.HasFinish ? map.FinishRect.ToString() : "未设置")}");
        foreach (SavePoint sp in map.Saves) { p.SendInfoMessage($"  存档{sp.Index}: {sp.Rect}"); }
    }
    /// <summary>清掉某个区域（在跑酷世界里执行）。</summary>
    private void ClearCommand(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }
        if (!p.HasPermission("parkour.admin")) { p.SendErrorMessage("需要 parkour.admin 权限"); return; }

        IMiniGameRoom? room = service!.FindByPlayer(p.Index);
        if (room?.Map is not ParkourMap map) { p.SendErrorMessage("你不在跑酷世界里（先 /pk build <图>）"); return; }
        if (args.Parameters.Count < 2) { p.SendErrorMessage("用法: /pk clear start|spawn|finish|saves|activity|all"); return; }

        switch (args.Parameters[1].ToLowerInvariant())
        {
            case "start": map.Start = Array.Empty<int>(); map.StartSpawn = Array.Empty<int>(); break;
            case "spawn": map.StartSpawn = Array.Empty<int>(); break;
            case "finish": map.Finish = Array.Empty<int>(); break;
            case "saves": map.Saves.Clear(); break;
            case "activity": map.Activity = Array.Empty<int>(); break;
            case "all":
                map.Start = Array.Empty<int>(); map.StartSpawn = Array.Empty<int>();
                map.Finish = Array.Empty<int>(); map.Saves.Clear(); map.Activity = Array.Empty<int>();
                break;
            default: p.SendErrorMessage("只能清 start / spawn / finish / saves / activity / all"); return;
        }

        p.SendSuccessMessage(ParkourMapStore.Save(map, out string path) ? $"[跑酷] 已清除并保存 → {path}" : "[跑酷] 保存失败（看日志）");
        game?.ReloadMaps();
    }

    /// <summary>改地图参数：倒计时 / 时限 / 宽限 / 容量。</summary>
    private void SetCommand(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }
        if (!p.HasPermission("parkour.admin")) { p.SendErrorMessage("需要 parkour.admin 权限"); return; }

        IMiniGameRoom? room = service!.FindByPlayer(p.Index);
        if (room?.Map is not ParkourMap map) { p.SendErrorMessage("你不在跑酷世界里（先 /pk build <图>）"); return; }
        if (args.Parameters.Count < 3 || !int.TryParse(args.Parameters[2], out int value) || value < 0 || value > 86400)
        {
            p.SendErrorMessage("用法: /pk set <countdown|time|grace|capacity> <秒/人数>");
            return;
        }

        switch (args.Parameters[1].ToLowerInvariant())
        {
            case "countdown": map.CountdownSeconds = value; break;
            case "time": map.TimeLimitSeconds = value; break;
            case "grace": map.GraceSeconds = value; break;
            case "capacity": map.Capacity = Math.Max(1, value); break;
            default: p.SendErrorMessage("只能设 countdown / time / grace / capacity"); return;
        }

        p.SendSuccessMessage(ParkourMapStore.Save(map, out string path) ? $"[跑酷] 已更新并保存 → {path}" : "[跑酷] 保存失败（看日志）");
        game?.ReloadMaps();
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        try { paintTimer?.Dispose(); } catch { }
        // 宿主不会替我们摘指令：不摘会导致热重载后 /pk 执行 N 次、旧程序集无法回收
        try
        {
            Command? cmd = Commands.ChatCommands.FirstOrDefault(c => c.HasAlias("pk"));
            if (cmd is not null) { Commands.ChatCommands.Remove(cmd); }
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    private static async Task RunAsync(TSPlayer? player, Func<Task<MiniGameJoinResult>> action)
    {
        if (player is null) { TShock.Log.Warning("[跑酷] 调用者不是玩家"); return; }
        try
        {
            MiniGameJoinResult r = await action().ConfigureAwait(false);
            if (!r.Ok)
            {
                player.SendErrorMessage("[跑酷] " + r.Reason);
                TShock.Log.Warning($"[跑酷] 失败: {r.Reason}");
            }
            else
            {
                player.SendInfoMessage($"[跑酷] 已进入（房间 {r.RoomId}）");
            }
        }
        catch (Exception ex)
        {
            player.SendErrorMessage("[跑酷] 异常: " + ex.Message);
            TShock.Log.Error("[跑酷] 命令异常: " + ex);
        }
    }
}