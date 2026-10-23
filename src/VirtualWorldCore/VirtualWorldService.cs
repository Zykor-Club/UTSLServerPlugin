using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Servers;
using UnifierTSL.Surface.Hosting.Server;

namespace UTSL.VirtualWorld;

/// <summary>
/// IVirtualWorldCore 实现：① 实例注册表 ② 生命周期(创建/加载/销毁) ③ 玩家进出封装。
/// ★ 三条铁律（见 50 号草案第 4 节）：
///   ① 只在转移【确实生效】后记账（GetClientCurrentlyServer 复核）；
///   ② 判"有没有人"以【世界玩家表】为准，不信自己维护的映射；
///   ③ 绝不对"状态看起来过期"的【在线玩家】触发生命周期钩子或清记录。
/// </summary>
internal sealed class VirtualWorldService : IVirtualWorldCore, IVirtualWorldStatsProvider
{
    /// <summary>诊断日志总开关（默认开，与改造前 PocketWorld 的 TransferDiagOn 一致）。</summary>
    internal static bool DiagnosticsEnabled = true;

    /// <summary>核心配置（启动时由插件载入；/vwc reload 可热更）。</summary>
    internal static VirtualWorldCoreConfig Config { get; set; } = new();

    private readonly ConcurrentDictionary<string, VirtualWorldInstance> worlds = new(StringComparer.Ordinal);

    /// <summary>每玩家的"返回路径"（后进先出）。Enter 时压入来源世界，Leave 时弹出。</summary>
    private readonly ConcurrentDictionary<int, List<ServerContext>> origins = new();

    /// <summary>最后一次使用的日志前缀（无请求上下文时的兜底）。</summary>
    private const string DefaultTag = "[VirtualWorldCore]";

    // 图格实体类型 ID（= 存档/网络协议里的稳定值，见 TrProtocol.Models.TileEntityType）
    private const int TeTrainingDummy = 0;
    private const int TeItemFrame = 1;
    private const int TeLogicSensor = 2;
    private const int TeDisplayDoll = 3;
    private const int TeWeaponsRack = 4;
    private const int TeHatRack = 5;
    private const int TeFoodPlatter = 6;

    // ==================== 模板约束（v8）====================

    /// <summary>玩法注册的模板约束；键 = 玩法组名（"*" 表示对所有玩法生效）。</summary>
    private static readonly ConcurrentDictionary<string, ITemplateGuard> guards = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterTemplateGuard(string group, ITemplateGuard? guard)
    {
        string key = NormalizeTemplateGroup(group);
        if (guard is null)
        {
            guards.TryRemove(key, out _);
            Info($"[模板约束] 已注销: {key}");
            return;
        }
        guards[key] = guard;
        Info($"[模板约束] 已注册: {key} ← {guard.GetType().Name}（共 {guards.Count} 条）");
    }

    private static string NormalizeTemplateGroup(string? group)
        => string.IsNullOrWhiteSpace(group) || group.Trim() == "*" ? "*" : group.Trim();

    /// <summary>
    /// 统一模板校验：① 玩法组/全局硬上限 → ② 玩法注册的 Guard。返回 null = 放行。
    /// 核心不再内置任何"玩法尺寸"，只防极端资源占用（硬上限默认 0 = 不限）。
    /// </summary>
    private static string? ValidateTemplateLimits(string? group, VirtualWorldTemplateInfo info, long bytes)
    {
        TemplateLimits lim = Config.LimitsFor(group);
        if (lim.Width > 0 && info.Width > lim.Width)
        {
            return $"模板宽度超限（{info.Width} > {lim.Width}，上限来自核心配置；玩法应自行约束尺寸）";
        }
        if (lim.Height > 0 && info.Height > lim.Height)
        {
            return $"模板高度超限（{info.Height} > {lim.Height}，上限来自核心配置；玩法应自行约束尺寸）";
        }
        if (lim.FileMB > 0 && bytes > 0 && bytes > lim.FileMB * 1024L * 1024L)
        {
            return $"模板文件过大（{bytes / 1024 / 1024}MB > {lim.FileMB}MB，上限来自核心配置）";
        }

        string g = NormalizeTemplateGroup(group);
        foreach (KeyValuePair<string, ITemplateGuard> kv in guards)
        {
            if (kv.Key != "*" && !string.Equals(kv.Key, g, StringComparison.OrdinalIgnoreCase)) { continue; }
            try
            {
                string? reason = kv.Value.ValidateTemplate(info, bytes, g);
                if (!string.IsNullOrEmpty(reason))
                {
                    Warn($"[模板约束] {kv.Key} 拒绝 '{info.Name}'（{info.Width}x{info.Height}，{bytes} 字节）: {reason}");
                    return reason;
                }
            }
            catch (Exception ex)
            {
                Warn($"[模板约束] {kv.Key} 抛异常（按放行处理）: {ex.Message}");
            }
        }
        return null;
    }
    // ==================== 注册表 ====================

    public IReadOnlyList<IVirtualWorld> Worlds => worlds.Values.Cast<IVirtualWorld>().ToArray();

    /// <summary>实例总览快照（可选接口 IVirtualWorldStatsProvider）。</summary>
    public VirtualWorldStats GetStats()
    {
        VirtualWorldInstance[] all = worlds.Values.ToArray();
        DateTime now = DateTime.UtcNow;
        List<VirtualWorldInstanceInfo> list = new(all.Length);
        int loaded = 0, occupants = 0;

        foreach (VirtualWorldInstance w in all)
        {
            int live = 0;
            try { live = CountLiveOccupants(w); } catch { }

            long bytes = 0;
            try
            {
                string? path = w.Context?.Main?.worldPathName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) { bytes = new FileInfo(path).Length; }
            }
            catch { }

            if (w.IsLoaded) { loaded++; }
            occupants += live;

            list.Add(new VirtualWorldInstanceInfo
            {
                Key = w.Key,
                DisplayName = w.DisplayName,
                Group = w.Group,
                OwnerKey = w.OwnerKey,
                TilesX = w.Context?.Main?.maxTilesX ?? 0,
                TilesY = w.Context?.Main?.maxTilesY ?? 0,
                Occupants = live,
                Loaded = w.IsLoaded,
                WorldFileBytes = bytes,
                IdleSeconds = Math.Max(0, (now - w.LastActiveUtc).TotalSeconds),
                TemplateKey = w.Request?.TemplateKey ?? "",
            });
        }

        return new VirtualWorldStats { Total = all.Length, Loaded = loaded, Occupants = occupants, Instances = list };
    }

    public IVirtualWorld? Find(string key) =>
        !string.IsNullOrEmpty(key) && worlds.TryGetValue(key, out VirtualWorldInstance? w) ? w : null;

    public IVirtualWorld? FindByOwner(string ownerKey) =>
        string.IsNullOrEmpty(ownerKey)
            ? null
            : worlds.Values.FirstOrDefault(w => string.Equals(w.OwnerKey, ownerKey, StringComparison.Ordinal));

    // ==================== 模板基线 ====================

    /// <summary>模板基线目录（&lt;Worlds&gt;/_vwc_templates）。</summary>
    private static string? TemplatesDir()
    {
        try
        {
            string probe = Utilities.IO.GetWorldPathFromName("_vwc_probe", true);
            string? dir = Path.GetDirectoryName(probe);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "_vwc_templates");
        }
        catch { return null; }
    }

    public IReadOnlyList<string> ListTemplates(int tilesX, int tilesY)
    {
        List<string> names = [];
        try
        {
            string? dir = TemplatesDir();
            if (dir is not null && Directory.Exists(dir))
            {
                string suffix = $"_" + tilesX + "x" + tilesY + ".wld";
                foreach (string f in Directory.GetFiles(dir, "*.wld"))
                {
                    string n = Path.GetFileName(f);
                    if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { names.Add(n[..^suffix.Length]); }
                }
            }
        }
        catch (Exception ex) { Warn($"[模板] 列举失败: {ex.Message}"); }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public async Task<VirtualWorldResult> SaveTemplateAsync(IVirtualWorld world, string name, CancellationToken ct)
    {
        ServerContext? ctx = world?.Context;
        if (ctx is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "实例未加载，无法导出模板"); }

        string safe = (name ?? "").Trim();
        if (safe.Length == 0) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, "模板名不能为空"); }
        foreach (char c in Path.GetInvalidFileNameChars()) { safe = safe.Replace(c, '_'); }

        int tx = ctx.Main.maxTilesX, ty = ctx.Main.maxTilesY;
        string? saveGuard = ValidateTemplateLimits(world.Group, new VirtualWorldTemplateInfo(safe, tx, ty), -1);
        if (saveGuard is not null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, saveGuard); }

        string? dir = TemplatesDir();
        if (dir is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, "无法定位模板目录"); }

        try
        {
            // ★ 存档必须在该世界自己的更新线程上跑
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Dispatcher.Post(() =>
            {
                try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); tcs.TrySetResult(true); }
                catch (Exception exSave) { tcs.TrySetException(exSave); }
            });
            await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

            string? potFile = ctx.Main.worldPathName;
            if (string.IsNullOrEmpty(potFile) || !File.Exists(potFile))
            {
                return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, "找不到该实例的世界文件");
            }

            Directory.CreateDirectory(dir);
            string dstFile = Path.Combine(dir, safe + "_" + tx + "x" + ty + ".wld");
            try { File.Copy(potFile, dstFile, overwrite: true); }
            catch (Exception exCopy)
            {
                Warn($"[模板] File.Copy 失败({exCopy.GetType().Name})，改用流式复制");
                using FileStream srcFs = File.OpenRead(potFile);
                using FileStream dstFs = File.Create(dstFile);
                srcFs.CopyTo(dstFs);
            }
            Info($"[模板] 已导出模板基线: {dstFile}");
            return VirtualWorldResult.Ok;
        }
        catch (Exception ex)
        {
            Error($"[模板] 导出失败: {ex.Message}");
            return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message);
        }
    }

    // ==================== 区块建筑模板（.utpl，v7）====================

    /// <summary>区块模板目录（&lt;Worlds&gt;/_utpl_templates）。</summary>
    private static string? UtplDir()
    {
        try
        {
            string probe = Utilities.IO.GetWorldPathFromName("_vwc_probe", true);
            string? dir = Path.GetDirectoryName(probe);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "_utpl_templates");
        }
        catch { return null; }
    }

    public IReadOnlyList<VirtualWorldTemplateInfo> ListRegionTemplates()
    {
        List<VirtualWorldTemplateInfo> list = [];
        try
        {
            string? dir = UtplDir();
            if (dir is not null && Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir, "*" + UtplFormat.Extension))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    list.Add(UtplFormat.TryReadSize(f, out int w, out int h)
                        ? new VirtualWorldTemplateInfo(name, w, h)
                        : new VirtualWorldTemplateInfo(name, 0, 0));
                }
            }
        }
        catch (Exception ex) { Warn($"[区块模板] 列举失败: {ex.Message}"); }
        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public VirtualWorldTemplateInfo? FindRegionTemplate(string name)
    {
        string? safe = UtplFormat.SanitizeName(name);
        if (safe is null) { return null; }
        string? dir = UtplDir();
        if (dir is null) { return null; }
        string file = Path.Combine(dir, safe + UtplFormat.Extension);
        if (!File.Exists(file)) { return null; }
        return UtplFormat.TryReadSize(file, out int w, out int h)
            ? new VirtualWorldTemplateInfo(safe, w, h)
            : new VirtualWorldTemplateInfo(safe, 0, 0);
    }

    /// <summary>【自检用】直接把 .utpl 读成内存快照（不走世界线程）。</summary>
    internal UtplRegion ReadTemplateForTest(string path) => UtplFormat.Read(path);

    public async Task<VirtualWorldResult> SaveRegionAsync(IVirtualWorld world, string name, int x, int y, int w, int h, CancellationToken ct)
    {
        ServerContext? ctx = (world as VirtualWorldInstance)?.Context;
        if (ctx is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "实例未加载，无法导出区块模板"); }
        if (w <= 0 || h <= 0) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, $"矩形尺寸非法: {w}x{h}"); }
        string? regionGuard = ValidateTemplateLimits(world.Group, new VirtualWorldTemplateInfo(name ?? "", w, h), -1);
        if (regionGuard is not null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, regionGuard); }

        string? safe = UtplFormat.SanitizeName(name);
        if (safe is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, "模板名不能为空或含非法字符"); }
        string? dir = UtplDir();
        if (dir is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, "无法定位区块模板目录"); }

        try
        {
            TaskCompletionSource<UtplRegion?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Dispatcher.Post(() =>
            {
                try { tcs.TrySetResult(CaptureRegion(ctx, x, y, w, h)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            UtplRegion? region = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            if (region is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, "矩形与世界没有交集，未导出"); }

            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, safe + UtplFormat.Extension);
            await Task.Run(() => UtplFormat.Write(dst, region), ct).ConfigureAwait(false);
            Info($"[区块模板] 已导出: {dst}（{region.W}x{region.H}，箱子={region.Chests.Count}，标牌={region.Signs.Count}，实体={region.Entities.Count}）");
            return VirtualWorldResult.Ok;
        }
        catch (Exception ex)
        {
            Error($"[区块模板] 导出失败: {ex}");
            return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message);
        }
    }

    public async Task<VirtualWorldResult> ApplyTemplateAsync(IVirtualWorld world, string name, int x, int y, CancellationToken ct)
    {
        ServerContext? ctx = (world as VirtualWorldInstance)?.Context;
        if (ctx is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "实例未加载，无法粘贴区块模板"); }

        string? safe = UtplFormat.SanitizeName(name);
        if (safe is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, "模板名不能为空或含非法字符"); }
        string? dir = UtplDir();
        if (dir is null) { return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, "无法定位区块模板目录"); }
        string src = Path.Combine(dir, safe + UtplFormat.Extension);
        if (!File.Exists(src)) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, $"模板不存在: {safe}"); }
        long srcBytes = -1;
        try { srcBytes = new FileInfo(src).Length; } catch { }
        string? applyBytesGuard = ValidateTemplateLimits(world.Group, new VirtualWorldTemplateInfo(safe, 0, 0), srcBytes);
        if (applyBytesGuard is not null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, applyBytesGuard); }

        try
        {
            UtplRegion region = await Task.Run(() => UtplFormat.Read(src), ct).ConfigureAwait(false);
            string? applySizeGuard = ValidateTemplateLimits(world.Group, new VirtualWorldTemplateInfo(safe, region.W, region.H), srcBytes);
            if (applySizeGuard is not null) { return VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, applySizeGuard); }

            // ★ 坐标口径（踩过坑，务必保持）：.utpl 的箱子/标牌/实体坐标全部是【模板内相对坐标】，
            //   文件里不含世界原点；贴入时一律 (tx + 记录坐标)。曾经箱子存绝对、标牌存相对，
            //   导致箱子被当成越界全部裁掉。
            region.X = 0;
            region.Y = 0;

            var tcs = new TaskCompletionSource<(int tiles, int chests, int signs, int ents)>(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Dispatcher.Post(() =>
            {
                try { StringBuilder diag = new(); var r = PasteRegion(ctx, region, x, y, diag); LastPasteDiag = diag.ToString(); tcs.TrySetResult(r); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            (int tiles, int chests, int signs, int ents) = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            Info($"[区块模板] 已粘贴 '{safe}' 到 ({x},{y})：图格={tiles}，箱子={chests}，标牌={signs}，实体={ents}");
            if (LastPasteDiag.Length > 0) { Warn($"[区块模板] 粘贴诊断: {LastPasteDiag}"); }
            return VirtualWorldResult.Ok;
        }
        catch (Exception ex)
        {
            Error($"[区块模板] 粘贴失败: {ex}");
            return VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message);
        }
    }

    // ==================== 区块模板：捕获 / 粘贴（★ 必须在本世界线程上跑）====================

    /// <summary>捕获矩形快照。返回 null = 矩形与世界无交集。</summary>
    private static UtplRegion? CaptureRegion(ServerContext ctx, int x, int y, int w, int h)
    {
        int maxX = ctx.Main.maxTilesX, maxY = ctx.Main.maxTilesY;
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(maxX, Math.Max(0, x) + w), y1 = Math.Min(maxY, Math.Max(0, y) + h);
        if (x1 <= x0 || y1 <= y0) { return null; }

        int rw = x1 - x0, rh = y1 - y0;
        UtplRegion region = new() { X = x0, Y = y0, W = rw, H = rh, Tiles = new TileData[rw * rh] };
        for (int ly = 0; ly < rh; ly++)
        {
            for (int lx = 0; lx < rw; lx++) { region.Tiles[ly * rw + lx] = ctx.Main.tile[x0 + lx, y0 + ly]; }
        }

        // 箱子：只收【左上角落在矩形内】的（否则贴出来会缺一半）
        Chest[] chests = ctx.Main.chest;
        for (int i = 0; i < chests.Length; i++)
        {
            Chest? c = chests[i];
            // ★ 必须整个 2x2 都在矩形内，否则记录坐标会落在 region 之外 → 贴入时按相对偏移算会错位
            if (c is null || c.x < x0 || c.x + 1 >= x1 || c.y < y0 || c.y + 1 >= y1) { continue; }
            // ★ 记录坐标一律存【模板内相对坐标】（= 世界坐标 - 矩形左上角），
            //   这样 .utpl 不带世界原点也能正确贴到任意位置（三个记录段必须同口径）。
            UtplChest uc = new() { X = c.x - x0, Y = c.y - y0, Name = c.name ?? "" };
            if (c.item is not null)
            {
                for (int s = 0; s < UtplFormat.ChestSlots && s < c.item.Length; s++)
                {
                    Item? it = c.item[s];
                    if (it is null || it.type <= 0) { continue; }
                    uc.Items[s] = (it.type, Math.Max(1, it.stack), it.prefix);
                }
            }
            region.Chests.Add(uc);
        }

        // 标牌
        Sign[] signs = ctx.Main.sign;
        for (int i = 0; i < signs.Length; i++)
        {
            Sign? s = signs[i];
            if (s is null || s.x < x0 || s.x >= x1 || s.y < y0 || s.y >= y1) { continue; }
            region.Signs.Add(new UtplSign { X = s.x - x0, Y = s.y - y0, Text = s.text ?? "" });
        }

        // v2 实体：物品框 / 盘子 / 人偶 / 帽架 / 逻辑感应器 / 武器架
        foreach (KeyValuePair<Point16, TileEntity> kv in ctx.TileEntity.ByPosition)
        {
            TileEntity ent = kv.Value;
            if (ent is null) { continue; }
            int ex = ent.Position.X, ey = ent.Position.Y;
            if (ex < x0 || ex >= x1 || ey < y0 || ey >= y1) { continue; }
            int lx = ex - x0, ly = ey - y0;   // 同样存模板内相对坐标
            UtplEntity? ue = ent switch
            {
                TEItemFrame frame => MakeEntity(UtplFormat.KindItemFrame, lx, ly, [frame.item]),
                TEWeaponsRack rack => MakeEntity(UtplFormat.KindWeaponsRack, lx, ly, [rack.item]),
                TEFoodPlatter platter => MakeEntity(UtplFormat.KindFoodPlatter, lx, ly, [platter.item]),
                TEHatRack hatRack => MakeEntity(UtplFormat.KindHatRack, lx, ly, Concat(hatRack._items, hatRack._dyes)),
                TEDisplayDoll doll => MakeEntity(UtplFormat.KindDisplayDoll, lx, ly, Concat(doll._equip, doll._dyes)),
                TELogicSensor ls => new UtplEntity { Kind = UtplFormat.KindLogicSensor, X = lx, Y = ly, On = ls.On },
                _ => null,
            };
            if (ue is not null) { region.Entities.Add(ue); }
        }

        return region;
    }

    private static UtplEntity MakeEntity(byte kind, int x, int y, Item?[] items)
    {
        UtplEntity e = new() { Kind = kind, X = x, Y = y };
        foreach (Item? it in items)
        {
            e.Slots.Add(it is null || it.type <= 0 ? (0, 0, 0) : (it.type, Math.Max(1, it.stack), it.prefix));
        }
        return e;
    }

    private static Item?[] Concat(Item[]? a, Item[]? b)
    {
        int na = a?.Length ?? 0, nb = b?.Length ?? 0;
        Item?[] r = new Item?[na + nb];
        for (int i = 0; i < na; i++) { r[i] = a![i]; }
        for (int i = 0; i < nb; i++) { r[na + i] = b![i]; }
        return r;
    }

    /// <summary>粘贴：先清空目标矩形（含旧箱子/标牌/实体），再写图格，最后重建箱子/标牌/实体。</summary>
    private static (int tiles, int chests, int signs, int ents) PasteRegion(ServerContext ctx, UtplRegion region, int tx, int ty, StringBuilder? diag = null)
    {
        int maxX = ctx.Main.maxTilesX, maxY = ctx.Main.maxTilesY;
        int px0 = Math.Max(0, tx), py0 = Math.Max(0, ty);
        int px1 = Math.Min(maxX, tx + region.W), py1 = Math.Min(maxY, ty + region.H);
        if (px1 <= px0 || py1 <= py0) { return (0, 0, 0, 0); }

        int tiles = 0;

        // ---- 1) 清空目标矩形 ----
        for (int wy = py0; wy < py1; wy++)
        {
            for (int wx = px0; wx < px1; wx++)
            {
                ctx.TileEntity.Kill(wx, wy, -1);        // 任何实体格
                ctx.Main.tile[wx, wy].ClearEverything();
            }
        }
        // 旧箱子 / 旧标牌（按左上角 / 牌坐标扫）
        Chest[] chests = ctx.Main.chest;
        for (int i = 0; i < chests.Length; i++)
        {
            Chest? c = chests[i];
            if (c is null || c.x < px0 || c.x > px1 - 1 || c.y < py0 || c.y > py1 - 1) { continue; }
            try { ctx.Chest.DestroyChest(c.x, c.y); } catch (Exception ex) { Warn($"[区块模板] 清旧箱子({c.x},{c.y})失败: {ex.Message}"); }
        }
        Sign[] signs = ctx.Main.sign;
        for (int i = 0; i < signs.Length; i++)
        {
            Sign? s = signs[i];
            if (s is null || s.x < px0 || s.x >= px1 || s.y < py0 || s.y >= py1) { continue; }
            try { Sign.KillSign(ctx, s.x, s.y); } catch (Exception ex) { Warn($"[区块模板] 清旧标牌({s.x},{s.y})失败: {ex.Message}"); }
        }

        // ---- 2) 写图格 ----
        for (int wy = py0; wy < py1; wy++)
        {
            int ly = wy - ty;
            for (int wx = px0; wx < px1; wx++)
            {
                int lx = wx - tx;
                TileData src = region.At(lx, ly);
                ref TileData dst = ref ctx.Main.tile[wx, wy];
                if (!UtplFormat.IsEmpty(src)) { tiles++; }
                dst = src;
            }
        }

        // ---- 3) 重建箱子 ----
        int chestCount = 0;
        foreach (UtplChest uc in region.Chests)
        {
            // 记录坐标 = 模板内相对坐标 → 加贴入点即可
            int cx = tx + uc.X, cy = ty + uc.Y;
            if (cx < px0 || cx > px1 - 2 || cy < py0 || cy > py1 - 2)
            {
                diag?.Append($"箱子({cx},{cy})被裁 [tx={tx} ty={ty} region.XY=({region.X},{region.Y}) uc=({uc.X},{uc.Y}) 范围=[{px0},{px1}]x[{py0},{py1}]]; ");
                continue;   // 超出贴入范围：裁掉
            }
            try
            {
                int idx = ctx.Chest.CreateChest(cx, cy);
                if (idx < 0 || idx >= chests.Length) { Warn($"[区块模板] CreateChest({cx},{cy}) 返回 {idx}，跳过"); diag?.Append($"箱子({cx},{cy})CreateChest返回{idx}; "); continue; }
                Chest? c = chests[idx];
                if (c is null) { continue; }
                c.name = uc.Name ?? "";
                if (c.item is null || c.item.Length < UtplFormat.ChestSlots) { try { c.Resize(UtplFormat.ChestSlots); } catch { } }
                if (c.item is null) { continue; }
                for (int s = 0; s < UtplFormat.ChestSlots && s < c.item.Length; s++)
                {
                    (int type, int stack, int prefix) = uc.Items[s];
                    if (type <= 0) { c.item[s] = new Item(); continue; }
                    c.item[s] = new Item();
                    c.item[s].SetDefaults(ctx, type);
                    c.item[s].stack = Math.Max(1, stack);
                    c.item[s].prefix = (byte)prefix;
                }
                chestCount++;
            }
            catch (Exception ex) { Warn($"[区块模板] 建箱子({cx},{cy})失败: {ex.Message}"); }
        }

        // ---- 4) 重建标牌 ----
        int signCount = 0;
        foreach (UtplSign us in region.Signs)
        {
            int sx = tx + us.X, sy = ty + us.Y;
            if (sx < px0 || sx >= px1 || sy < py0 || sy >= py1) { continue; }
            try
            {
                if (sx < 0 || sy < 0 || sx >= ctx.Main.maxTilesX || sy >= ctx.Main.maxTilesY) { continue; }
                if (!ctx.Main.tile[sx, sy].active()) { diag?.Append($"标牌({sx},{sy})该格没图格; "); continue; }

                // ★★ 标牌的两个实测坑：
                //   1) 记录坐标是【Sign 对象坐标】。Sign 对象登记在牌子 frame=(0,0) 的那一格，
                //      而 PlaceSign 要的是"牌子基准格"——两者差一格，用 PlaceSign 很容易摆歪/摆不上。
                //   2) 图格已经从模板原样写好了 → 正确的补法是用 ReadSign(CreateIfMissing:=true)
                //      只补出 Sign 对象（它按 frameX/18、frameY/18 反推对象坐标，正好落在记录位置上）。
                int existing = Sign.ReadSign(ctx, sx, sy, false);
                if (existing < 0)
                {
                    existing = Sign.ReadSign(ctx, sx, sy, true);
                }
                if (existing < 0 || existing >= signs.Length || signs[existing] is null)
                {
                    Warn($"[区块模板] 标牌({sx},{sy}) 无法建立（ReadSign 返回 {existing}），跳过该标牌");
                    diag?.Append($"标牌({sx},{sy})建立失败(ReadSign={existing}); ");
                    continue;
                }
                signs[existing].text = us.Text ?? "";
                signCount++;
            }
            catch (Exception ex) { Warn($"[区块模板] 建标牌({sx},{sy})失败: {ex.Message}"); }
        }

        // ---- 5) 重建 v2 实体 ----
        int entCount = 0;
        foreach (UtplEntity ue in region.Entities)
        {
            int ex = tx + ue.X, ey = ty + ue.Y;
            if (ex < px0 || ex >= px1 || ey < py0 || ey >= py1) { continue; }
            try
            {
                if (!ctx.Main.tile[ex, ey].active()) { diag?.Append($"实体kind={ue.Kind}({ex},{ey})该格没图格; "); continue; }
                if (ue.Kind == UtplFormat.KindLogicSensor)
                {
                    if (!PlaceEntity(ctx, ex, ey, TeLogicSensor, out TileEntity? lsEnt)) { continue; }
                    if (lsEnt is TELogicSensor ls) { ls.On = ue.On; }
                    entCount++;
                    continue;
                }
                int type = ue.Kind switch
                {
                    UtplFormat.KindItemFrame => TeItemFrame,
                    UtplFormat.KindDisplayDoll => TeDisplayDoll,
                    UtplFormat.KindHatRack => TeHatRack,
                    UtplFormat.KindFoodPlatter => TeFoodPlatter,
                    UtplFormat.KindWeaponsRack => TeWeaponsRack,
                    _ => -1,
                };
                if (type < 0) { continue; }
                if (!PlaceEntity(ctx, ex, ey, type, out _)) { diag?.Append($"实体kind={ue.Kind}({ex},{ey})放置失败; "); continue; }
                Item?[]? slots = null;
                if (ue.Kind == UtplFormat.KindItemFrame && ctx.TileEntity.TryGetAt(ex, ey, out TEItemFrame frame) && frame is not null)
                {
                    slots = [frame.item];
                }
                else if (ue.Kind == UtplFormat.KindWeaponsRack && ctx.TileEntity.TryGetAt(ex, ey, out TEWeaponsRack wrack) && wrack is not null)
                {
                    slots = [wrack.item];
                }
                else if (ue.Kind == UtplFormat.KindFoodPlatter && ctx.TileEntity.TryGetAt(ex, ey, out TEFoodPlatter platter) && platter is not null)
                {
                    slots = [platter.item];
                }
                else if (ue.Kind == UtplFormat.KindHatRack && ctx.TileEntity.TryGetAt(ex, ey, out TEHatRack hatRack) && hatRack is not null)
                {
                    slots = [.. (hatRack._items ?? []), .. (hatRack._dyes ?? [])];
                }
                else if (ue.Kind == UtplFormat.KindDisplayDoll && ctx.TileEntity.TryGetAt(ex, ey, out TEDisplayDoll doll) && doll is not null)
                {
                    slots = [.. (doll._equip ?? []), .. (doll._dyes ?? [])];
                }
                if (slots is not null) { ApplyItems(ctx, ue, slots); }
                entCount++;
            }
            catch (Exception exMsg) { Warn($"[区块模板] 重建实体(kind={ue.Kind},{ex},{ey})失败: {exMsg.Message}"); }
        }

        return (tiles, chestCount, signCount, entCount);
    }

    /// <summary>
    /// 在 (x,y) 放一个图格实体。
    /// ★ 坑（实测）：<c>ctx.TileEntity.Place(x,y,type)</c> **其实放成功了，但返回值是 0**
    ///   （反编译确认：它 return 的是 ent.ID，而 Add 之前 ID 还是 0）→
    ///   绝对不能用 `<= 0` 判定失败，必须以 ByPosition 里"该坐标有没有实体"为准。
    /// </summary>
    private static bool PlaceEntity(ServerContext ctx, int x, int y, int type, out TileEntity? ent)
    {
        ent = null;
        try
        {
            ctx.TileEntity.Place(x, y, type);
            if (ctx.TileEntity.TryGetAt(x, y, out TileEntity got) && got is not null) { ent = got; return true; }
            Warn($"[区块模板] 实体放置失败: type={type} @({x},{y})（ByPosition 里没有）");
            return false;
        }
        catch (Exception ex)
        {
            Warn($"[区块模板] 实体放置异常: type={type} @({x},{y}) {ex.Message}");
            return false;
        }
    }

    /// <summary>区块模板目录（自检/诊断用）。</summary>
    internal static string? TemplatesDirForTest() => UtplDir();

    /// <summary>【自检用】上一次粘贴的详细诊断。</summary>
    internal static string LastPasteDiag = "";

    /// <summary>把记录里的槽位贴回目标 Item 数组（长度不匹配时贴能贴的部分）。</summary>
    private static void ApplyItems(UnifiedServerProcess.RootContext ctx, UtplEntity ue, Item?[] target)
    {
        int n = Math.Min(ue.Slots.Count, target.Length);
        for (int i = 0; i < n; i++)
        {
            (int type, int stack, int prefix) = ue.Slots[i];
            if (type <= 0) { continue; }
            Item it = new();
            it.SetDefaults(ctx, type);
            it.stack = Math.Max(1, stack);
            it.prefix = (byte)prefix;
            target[i] = it;
        }
    }

    internal VirtualWorldInstance? FindByContext(ServerContext? ctx) =>
        ctx is null ? null : worlds.Values.FirstOrDefault(w => ReferenceEquals(w.Context, ctx));

    internal bool Remove(string key) => worlds.TryRemove(key, out _);

    public event Action<IVirtualWorld>? WorldLoaded;
    public event Action<IVirtualWorld>? WorldDestroyed;
    public event Action<TSPlayer, IVirtualWorld>? PlayerEntered;
    public event Action<TSPlayer, IVirtualWorld>? PlayerLeft;
    public event Action<VirtualWorldTransferArgs>? Transferring;
    public event Action<VirtualWorldTransferArgs>? Transferred;
    public event Action<VirtualWorldDestroyArgs>? Destroying;

    // ==================== 生命周期：创建 / 加载 ====================

    public Task<IVirtualWorld> EnsureAsync(VirtualWorldRequest request, CancellationToken ct) =>
        Task.Run(() => EnsureCore(request), ct);

    private IVirtualWorld EnsureCore(VirtualWorldRequest request)
    {
        if (Config.MaxInstances > 0 && !worlds.ContainsKey(request.Key) && worlds.Count >= Config.MaxInstances)
        {
            throw new InvalidOperationException($"实例数已达上限（{Config.MaxInstances}），拒绝创建 {request.Key}");
        }

        VirtualWorldInstance w = worlds.GetOrAdd(request.Key,
            k => new VirtualWorldInstance(k, request.DisplayName, request.OwnerKey));
        w.Request = request;
        w.Group = request.Group;
        if (w.IsLoaded) { w.Touch(); return w; }

        lock (w.LoadGate)
        {
            if (w.IsLoaded) { w.Touch(); return w; }

            bool createdNow = false;
            bool seededNow = false;

            // ★ 与改造前一致：只有 Context 为 null 才【新建】世界；已有 Context 只等待它跑起来，
            //   绝不重复创建（否则会留下跑着的孤儿世界）
            if (w.Context is null)
            {
                string worldFile = Utilities.IO.GetWorldPathFromName(request.WorldFileName, true);

                // 目录可写性自检：写不进去时给出明确告警（世界文件创建失败时，问题多半在这里）
                try
                {
                    string? pdir = Path.GetDirectoryName(worldFile);
                    string probe = Path.Combine(pdir ?? "", "_vwwprobe.tmp");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                }
                catch (Exception pe)
                {
                    Error($"{Tag(w)} 探测: WorldPath='{Terraria.Main.WorldPath}' worldFile='{worldFile}' exists={File.Exists(worldFile)} seed='{request.SeedWorldPath}' seedExists={(!string.IsNullOrEmpty(request.SeedWorldPath) && File.Exists(request.SeedWorldPath))} ★目录不可写: {pe.GetType().Name}: {pe.Message}");
                }

                if (!File.Exists(worldFile))
                {
                    string? dir = Path.GetDirectoryName(worldFile);
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

                    // ★ B：优先用【同尺寸的模板基线】（很小的模板文件）→ 不用复制大地图，也不用再全图清空
                    string? templatePath = TemplatePathFor(request, worldFile);
                    if (templatePath is not null && File.Exists(templatePath))
                    {
                        CopyFile(templatePath, worldFile, w, "模板基线");
                        seededNow = false;
                        Info($"{Tag(w)} 已用模板基线建立 {worldFile}（{request.TilesX}x{request.TilesY}）");
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(request.SeedWorldPath))
                        {
                            throw new InvalidOperationException($"世界文件不存在且未提供种子: {worldFile}");
                        }
                        if (!File.Exists(request.SeedWorldPath))
                        {
                            throw new FileNotFoundException($"种子世界文件不存在: {request.SeedWorldPath}");
                        }

                        CopyFile(request.SeedWorldPath, worldFile, w, "种子世界文件");
                        seededNow = true;
                        Info($"{Tag(w)} 已用种子世界文件建立 {worldFile}");
                    }
                }

                ServerContext created = CreateContext(w, request);
                try
                {
                    created.Run([]);
                }
                catch (Exception ex)
                {
                    Remove(w.Key);
                    throw new InvalidOperationException($"建立世界失败: {ex.Message}", ex);
                }

                UnifiedServerCoordinator.AddServer(created);
                w.Context = created;
                w.WasJustSeeded = seededNow;
                createdNow = true;
            }

            ServerContext ctx = w.Context!;

            int waited = 0;
            // ★ A：原来 Thread.Sleep(500) 轮询 → 每次加载平均白等 ~250ms；改成 25ms
            while (waited < 120000 && !ctx.IsRunning) { Thread.Sleep(25); waited += 25; }
            if (!ctx.IsRunning)
            {
                // 启动超时：不但不登记，还要把已创建的世界收掉，避免留下跑着的孤儿世界
                w.Context = null;
                Remove(w.Key);
                try { ctx.Close().Wait(TimeSpan.FromSeconds(Math.Max(1, Config.DestroyCloseTimeoutSeconds))); } catch { }
                try { UnifiedServerCoordinator.RemoveServer(ctx); } catch { }
                throw new TimeoutException($"世界启动超时: {ctx.Name}");
            }

            // OnLoaded 必须在【本世界线程】上跑（改 Main / 贴模板）；只在【本次真的新建了世界】时跑一次
            if (createdNow)
            {
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    ctx.Dispatcher.Post(() =>
                    {
                        try
                        {
                            request.OnLoaded?.Invoke(ctx, seededNow);
                            loaded.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            Error($"{Tag(w)} OnLoaded 失败: {ex}");
                            loaded.TrySetResult(false);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Error($"{Tag(w)} OnLoaded 投递失败: {ex.Message}");
                    loaded.TrySetResult(false);
                }
                if (!loaded.Task.Wait(TimeSpan.FromSeconds(Math.Max(1, Config.LoadTimeoutSeconds))))
                {
                    Warn($"{Tag(w)} OnLoaded 等待超时（世界可能空转，Post 队列需空闲驱动）");
                }
            }

            // ★ B：首次创建时，把"玩法初始化完成后的世界"缓存为该尺寸的模板基线，供后续同尺寸实例秒建
            if (createdNow && !string.IsNullOrEmpty(request.TemplateKey))
            {
                try
                {
                    string? templatePath = TemplatePathFor(request, Utilities.IO.GetWorldPathFromName(request.WorldFileName, true));
                    if (templatePath is not null && !File.Exists(templatePath))
                    {
                        // ★ 这里【不要再 SaveWorld】：玩法在 OnLoaded 里应该已经把基线状态落盘了
                        //   （例如 ClearCurrentWorld() 内部就会 SaveWorld，见 ServerContext.World.cs:130-135）。
                        //   重复存盘要序列化整张图（10M 格，实测 ~0.8s），纯属浪费。
                        string? tdir = Path.GetDirectoryName(templatePath);
                        if (!string.IsNullOrEmpty(tdir)) { Directory.CreateDirectory(tdir); }
                        File.Copy(Utilities.IO.GetWorldPathFromName(request.WorldFileName, true), templatePath, overwrite: true);
                        Info($"{Tag(w)} 已缓存模板基线: {templatePath}");
                    }
                }
                catch (Exception ex) { Warn($"{Tag(w)} 缓存模板基线失败: {ex.Message}"); }
            }

            Info($"{Tag(w)} 世界已加载: {ctx.Name}（{request.TilesX}x{request.TilesY}，刚播种={seededNow}）");
            WorldLoaded?.Invoke(w);
        }

        return w;
    }

    /// <summary>模板基线文件路径（按 TemplateKey + 尺寸区分）；未启用返回 null。</summary>
    private static string? TemplatePathFor(VirtualWorldRequest request, string worldFile)
    {
        if (string.IsNullOrEmpty(request.TemplateKey)) { return null; }
        string? baseDir = Path.GetDirectoryName(worldFile);
        if (string.IsNullOrEmpty(baseDir)) { return null; }
        string safe = request.TemplateKey;
        foreach (char c in Path.GetInvalidFileNameChars()) { safe = safe.Replace(c, '_'); }
        return Path.Combine(baseDir, "_vwc_templates", $"{safe}_{request.TilesX}x{request.TilesY}.wld");
    }

    private static void CopyFile(string from, string to, VirtualWorldInstance w, string what)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception exCopy)
        {
            Warn($"[{w.Group}] {what} File.Copy 失败({exCopy.GetType().Name})，改用流式复制");
            using FileStream src = File.OpenRead(from);
            using FileStream dst = File.Create(to);
            src.CopyTo(dst);
        }
    }

    private static ServerContext CreateContext(VirtualWorldInstance w, VirtualWorldRequest request)
    {
        string name = string.IsNullOrEmpty(request.ServerName)
            ? "VW_" + ShortKey(request.Key)
            : request.ServerName;
        IWorldDataProvider provider = BuildProvider(request);
        return request.Headless
            ? new HeadlessWorldServer(name, provider)
            : new ServerContext(name, provider);
    }

    private static IWorldDataProvider BuildProvider(VirtualWorldRequest request)
    {
        // 高度通过 UTSL_CUSTOM_HEIGHT 传入（与改造前 PocketWorld.BuildProvider 完全一致）
        string? prev = Environment.GetEnvironmentVariable("UTSL_CUSTOM_HEIGHT");
        try
        {
            Environment.SetEnvironmentVariable("UTSL_CUSTOM_HEIGHT", request.TilesY.ToString());
            return IWorldDataProvider.GenerateOrLoadExisting(
                request.WorldFileName, request.TilesX, difficulty: 0, worldEvil: 0, seed: "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UTSL_CUSTOM_HEIGHT", prev);
        }
    }

    // ==================== 生命周期：销毁 ====================

    public Task<VirtualWorldResult> DestroyAsync(IVirtualWorld world, bool save, CancellationToken ct)
    {
        if (world is not VirtualWorldInstance w)
        {
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "实例不存在"));
        }
        ServerContext? ctx = w.Context;
        if (ctx is null)
        {
            Remove(w.Key);
            return Task.FromResult(VirtualWorldResult.Ok);
        }

        // ★ 不变式：任何玩家"要回去的世界"都不能被回收，否则他 exit 时回不去（假世界嵌套场景）
        if (IsOnAnyReturnStack(ctx))
        {
            w.Touch();
            Warn($"{Tag(w)} 销毁被否决：仍在某玩家的返回路径上");
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.OnReturnPath, "仍在某玩家的返回路径上"));
        }

        // ★ 铁律②：以【世界玩家表】为准；世界里还有 active 玩家 → 绝不销毁（双保险）
        if (HasActivePlayers(ctx))
        {
            w.Touch();
            Warn($"{Tag(w)} 销毁被否决：世界玩家表里仍有 active 玩家");
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.Occupied, "世界里仍有 active 玩家"));
        }
        IVirtualWorldPolicy? pol = w.Request.Policy;
        if (pol is not null)
        {
            bool can = true;
            try { can = pol.CanDestroy(world); } catch { can = true; }
            if (!can) { w.Touch(); return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, "玩法策略否决销毁")); }
        }

        if (Destroying is not null)
        {
            VirtualWorldDestroyArgs destroyArgs = new() { World = world };
            foreach (Action<VirtualWorldDestroyArgs> h in Destroying.GetInvocationList())
            {
                try { h(destroyArgs); } catch { }
                if (destroyArgs.Cancel)
                {
                    w.Touch();
                    return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.EventCanceled, destroyArgs.CancelReason ?? "被事件监听方否决"));
                }
            }
        }

        var done = new TaskCompletionSource<VirtualWorldResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ctx.Dispatcher.Post(() =>
            {
                try
                {
                    if (save)
                    {
                        try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); }
                        catch (Exception ex) { Warn($"{Tag(w)} 存档失败: {ex.Message}"); }
                    }

                    // ★ 关世界前必须处理仍绑定在该世界上的连接：否则 socket 还连着、指针还指着已死世界，
                    //   入站/出站闸门双双失效 → 命令与聊天全哑；TShock 侧还会留下悬空条目（同名两个我）。
                    for (int ci = 0; ci < 256; ci++)
                    {
                        try
                        {
                            if (!ReferenceEquals(UnifiedServerCoordinator.GetClientCurrentlyServer(ci), ctx)) { continue; }
                            TSPlayer? ctp = TShock.Players.ElementAtOrDefault(ci);
                            if (ctp is not null && ctp.Active)
                            {
                                continue;
                            }
                            if (TShock.Players[ci] is not null) { TShock.Players[ci] = null; }
                            Warn($"{Tag(w)} 已清理悬空玩家条目 #{ci}（卸载 {w.DisplayName} 前）");
                        }
                        catch { }
                    }
                    posted.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Error($"{Tag(w)} 卸载前处理失败: {ex}");
                    posted.TrySetResult(false);
                }
            });
        }
        catch (Exception ex)
        {
            Error($"{Tag(w)} 卸载投递失败: {ex.Message}");
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotRunning, ex.Message));
        }

        // ★ 关键：Close() 内部会 Join 世界线程；如果像旧实现那样在 Post 回调（跑在世界线程上）里调它，
        //   就是『自己等自己』→ 必然卡满 Wait 超时（实测 60 秒）。所以先等世界线程把存档/清理做完，
        //   再在【调用方线程】上 Close。
        long t0 = Environment.TickCount64;
        int postTimeout = Math.Max(1, Config.DestroyPostTimeoutSeconds);
        if (!posted.Task.Wait(TimeSpan.FromSeconds(postTimeout)))
        {
            Warn($"{Tag(w)} 卸载前任务超时（{postTimeout}s），继续强制卸载");
        }
        try
        {
            Task closeTask = ctx.Close();
            int closeTimeout = Math.Max(1, Config.DestroyCloseTimeoutSeconds);
            if (!closeTask.Wait(TimeSpan.FromSeconds(closeTimeout)))
            {
                Warn($"{Tag(w)} Close 超时（{closeTimeout}s）：世界线程可能仍在睡眠，先继续清理");
            }
        }
        catch (Exception ex) { Warn($"{Tag(w)} Close 异常: {ex.Message}"); }
        Info($"{Tag(w)} 关闭世界耗时 {Environment.TickCount64 - t0} ms");

        try { UnifiedServerCoordinator.RemoveServer(ctx); } catch (Exception ex) { Warn($"{Tag(w)} RemoveServer 异常: {ex.Message}"); }

        w.Context = null;
        Remove(w.Key);
        Info($"{Tag(w)} {w.DisplayName} 已成功卸载。");
        WorldDestroyed?.Invoke(w);
        done.TrySetResult(VirtualWorldResult.Ok);
        return done.Task;
    }

    /// <summary>该世界是否还挂在某个玩家的返回路径（返回栈）上。</summary>
    private bool IsOnAnyReturnStack(ServerContext ctx)
    {
        foreach (KeyValuePair<int, List<ServerContext>> kv in origins)
        {
            lock (kv.Value)
            {
                foreach (ServerContext s in kv.Value)
                {
                    if (ReferenceEquals(s, ctx)) { return true; }
                }
            }
        }
        return false;
    }

    private static bool HasActivePlayers(ServerContext ctx)
    {
        try
        {
            for (int pi = 0; pi < ctx.Main.player.Length; pi++)
            {
                var pp = ctx.Main.player[pi];
                if (pp is not null && pp.active) { return true; }
            }
        }
        catch { }
        return false;
    }

    // ==================== 玩家进出 ====================

    public Task<VirtualWorldResult> EnterAsync(TSPlayer player, IVirtualWorld world, CancellationToken ct)
    {
        if (player is null || world is not VirtualWorldInstance w)
        {
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "实例不存在"));
        }

        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(player.Index);
        ServerContext? target = w.Context;
        if (from is null) { return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "找不到玩家当前所在的世界")); }
        if (target is null) { return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotLoaded, "实例尚未加载")); }
        if (!target.IsRunning) { return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotRunning, "目标世界未运行")); }
        if (ReferenceEquals(from, target)) { return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.SameWorld, "玩家已在目标世界")); }

        IVirtualWorldPolicy? pol = w.Request.Policy;
        if (pol is not null)
        {
            bool can = true;
            try { can = pol.CanJoin(player, world); } catch { can = true; }
            if (!can) { return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.PolicyRejected, "玩法策略否决进入")); }
        }

        VirtualWorldTransferArgs enterArgs = new() { Player = player, World = world, IsEnter = true };
        VirtualWorldResult enterVeto = RaiseTransferring(Transferring, enterArgs);
        if (!enterVeto.Success) { return Task.FromResult(enterVeto); }

        var done = new TaskCompletionSource<VirtualWorldResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // 先在目标世界线程做准备（贴模板/铺平台），再回来源世界线程搬人 —— 与改造前逐字一致（R8 取舍）
            target.Dispatcher.Post(() =>
            {
                try
                {
                    w.Request.OnBeforeEnter?.Invoke(target, w.WasJustSeeded);
                    from.Dispatcher.Post(() =>
                    {
                        try
                        {
                            UnifiedServerCoordinator.TransferPlayerToServer((byte)player.Index, target);

                            // ★ 铁律①：只有复核通过才记账
                            if (!ReferenceEquals(UnifiedServerCoordinator.GetClientCurrentlyServer(player.Index), target))
                            {
                                Warn($"{Tag(w)} 进入未生效（转移被拒绝）plr={player.Index} target={target.Name}");
                                done.TrySetResult(VirtualWorldResult.Fail(VirtualWorldFailure.TransferRejected, "世界转移被拒绝（生效复核不通过）"));
                                return;
                            }

                            RegisterAt(player.Index, target);   // 清掉其它实例上的旧记录 + 登记本次落脚点
                            PushOrigin(player.Index, from);
                            Info($"{Tag(w)} {player.Name} 进入 {w.DisplayName}");
                            Diag("enter", player.Index, from, target, w);
                            DiagDelayed("enter", player.Index, target, w);

                            SafeEntered(pol, player, world);
                            PlayerEntered?.Invoke(player, world);
                            try { Transferred?.Invoke(new VirtualWorldTransferArgs { Player = player, World = world, IsEnter = true }); } catch { }
                            done.TrySetResult(VirtualWorldResult.Ok);
                        }
                        catch (Exception ex)
                        {
                            Error($"{Tag(w)} 搬人失败: {ex}");
                            done.TrySetResult(VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message));
                        }
                    });
                }
                catch (Exception ex)
                {
                    Error($"{Tag(w)} 进入准备失败: {ex}");
                    done.TrySetResult(VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            Error($"{Tag(w)} 进入投递失败: {ex.Message}");
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotRunning, ex.Message));
        }
        return done.Task;
    }

    public Task<VirtualWorldResult> LeaveAsync(TSPlayer player, CancellationToken ct)
    {
        if (player is null) { return Task.FromResult(VirtualWorldResult.Ok); }
        int idx = player.Index;

        ServerContext? origin = PeekOrigin(idx);
        ServerContext? here = UnifiedServerCoordinator.GetClientCurrentlyServer(idx);
        VirtualWorldInstance? w = FindByContext(here);

        // 没有记录 → 幂等返回（与改造前一致：清掉我们自己的记录，不动玩家槽位）
        if (origin is null)
        {
            ClearOccupancy(idx);
            return Task.FromResult(VirtualWorldResult.Ok);
        }

        // 已经在来源世界 → 只清记录（自愈；与改造前一致，不按落点重新登记）
        if (here is null || ReferenceEquals(here, origin))
        {
            PopOrigin(idx);
            ClearOccupancy(idx);
            return Task.FromResult(VirtualWorldResult.Ok);
        }

        if (w is not null)
        {
            VirtualWorldTransferArgs leaveArgs = new() { Player = player, World = w, IsEnter = false };
            VirtualWorldResult leaveVeto = RaiseTransferring(Transferring, leaveArgs);
            if (!leaveVeto.Success) { return Task.FromResult(leaveVeto); }
        }

        var done = new TaskCompletionSource<VirtualWorldResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            here.Dispatcher.Post(() =>
            {
                try
                {
                    UnifiedServerCoordinator.TransferPlayerToServer((byte)idx, origin);

                    // ★ 铁律①：复核失败就不清记录（状态不许与真实世界分叉）
                    if (!ReferenceEquals(UnifiedServerCoordinator.GetClientCurrentlyServer(idx), origin))
                    {
                        Warn($"{Tag(w)} 离开未生效（转移被拒绝）plr={idx} origin={origin.Name}");
                        done.TrySetResult(VirtualWorldResult.Fail(VirtualWorldFailure.TransferRejected, "世界转移被拒绝（生效复核不通过）"));
                        return;
                    }

                    PopOrigin(idx);
                    RegisterAt(idx, origin);   // ★ 落点是实例就重新登记（例如从别人的壶回到自己的壶）
                    w?.Touch();
                    Info($"{Tag(w)} {player.Name} 离开 {w?.DisplayName ?? "?"}");
                    Diag("leave", idx, here, origin, w);
                    DiagDelayed("leave", idx, origin, w);

                    if (w is not null)
                    {
                        SafeLeft(w.Request.Policy, player, w);
                        PlayerLeft?.Invoke(player, w);
                        try { Transferred?.Invoke(new VirtualWorldTransferArgs { Player = player, World = w, IsEnter = false }); } catch { }
                    }
                    done.TrySetResult(VirtualWorldResult.Ok);
                }
                catch (Exception ex)
                {
                    Error($"{Tag(w)} 返回失败: {ex}");
                    done.TrySetResult(VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            Error($"{Tag(w)} 返回投递失败: {ex.Message}");
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotRunning, ex.Message));
        }
        return done.Task;
    }

    /// <summary>把玩家从所有实例的占用表里摘除（绝不触碰玩家槽位）。</summary>
    private void ClearOccupancy(int idx)
    {
        foreach (VirtualWorldInstance v in worlds.Values) { v.RemoveOccupant(idx); }
    }

    /// <summary>把玩家登记到 dest：dest 是实例就记为占用者，否则从所有实例摘除。绝不动玩家槽位。</summary>
    private void RegisterAt(int idx, ServerContext dest)
    {
        VirtualWorldInstance? destWorld = FindByContext(dest);
        foreach (VirtualWorldInstance v in worlds.Values)
        {
            if (!ReferenceEquals(v, destWorld)) { v.RemoveOccupant(idx); }
        }
        if (destWorld is not null) { destWorld.AddOccupant(idx); destWorld.Touch(); }
    }

    // ==================== 退出 / 看护 / 自愈 ====================

    public IVirtualWorld? FindByOccupant(int playerIndex)
    {
        foreach (VirtualWorldInstance v in worlds.Values)
        {
            if (v.HasOccupant(playerIndex)) { return v; }
        }
        return null;
    }

    /// <summary>
    /// 真实在线占用数：只数【TShock 判定在线】且【路由指针确实指着本世界】的玩家。
    /// 这样即使记录里挂着已登出的玩家，界面与闲置判定也不会被拖住（铁律②）。
    /// </summary>
    public int CountLiveOccupants(IVirtualWorld world)
    {
        if (world is not VirtualWorldInstance w) { return 0; }
        int n = 0;
        foreach (int idx in w.Occupants)
        {
            try
            {
                TSPlayer? tp = TShock.Players.ElementAtOrDefault(idx);
                if (tp is null || !tp.Active) { continue; }
                if (!ReferenceEquals(UnifiedServerCoordinator.GetClientCurrentlyServer(idx), w.Context)) { continue; }
                n++;
            }
            catch { }
        }
        return n;
    }

    /// <summary>玩家登出：摘占用 + 清返回路径（不触碰玩家槽位）。返回登出时所在的实例（若有）。</summary>
    public IVirtualWorld? NotifyPlayerGone(int playerIndex)
    {
        VirtualWorldInstance? found = null;
        foreach (VirtualWorldInstance v in worlds.Values)
        {
            if (v.RemoveOccupant(playerIndex)) { found = v; }
        }
        ClearOrigins(playerIndex);
        return found;
    }

    /// <summary>自愈：记录说在某实例里，但实际不在该世界 → 清记录。只清【我们自己的记录】。</summary>
    public void SelfHeal(int idx)
    {
        ServerContext? cur = UnifiedServerCoordinator.GetClientCurrentlyServer(idx);
        bool stale = false;
        foreach (VirtualWorldInstance v in worlds.Values)
        {
            if (v.HasOccupant(idx) && !ReferenceEquals(v.Context, cur))
            {
                v.RemoveOccupant(idx);
                stale = true;
            }
        }
        if (stale)
        {
            ClearOrigins(idx);
            Info($"{DefaultTag} 已清理记录 plr={idx}（自愈）");
        }
    }

    /// <summary>对账（Janitor）：记录里挂着、但玩家已离线的索引 → 清记录。在线玩家一律不动（铁律③）。</summary>
    public void Reconcile()
    {
        foreach (VirtualWorldInstance w in worlds.Values)
        {
            foreach (int idx in w.Occupants.ToArray())
            {
                bool offline;
                try
                {
                    TSPlayer? tp = TShock.Players.ElementAtOrDefault(idx);
                    offline = tp is null || !tp.Active;
                }
                catch { offline = true; }

                if (!offline) { continue; }   // ★ 铁律③：在线玩家绝不清记录
                w.RemoveOccupant(idx);
                ClearOrigins(idx);
                Info($"{DefaultTag} 已清理记录 plr={idx}（已离线，{w.DisplayName}）");
            }
        }
    }

    /// <summary>状态一致性诊断：只在发现不一致时输出。</summary>
    public void StateCheck()
    {
        if (!DiagnosticsEnabled) { return; }
        try
        {
            foreach (VirtualWorldInstance w in worlds.Values)
            {
                foreach (int idx in w.Occupants.ToArray())
                {
                    TSPlayer? tp = TShock.Players.ElementAtOrDefault(idx);
                    bool offline = tp is null || !tp.Active;
                    ServerContext? cur = offline ? null : UnifiedServerCoordinator.GetClientCurrentlyServer(idx);
                    bool mismatch = offline || (cur is not null && !ReferenceEquals(cur, w.Context));
                    if (!mismatch) { continue; }
                    Warn($"{DefaultTag} 状态不一致: 记录 plr={idx} 在 {w.DisplayName}；" +
                        $"tshock={(tp is null ? "null" : tp.Active ? "在线" : "离线")} cur={cur?.Name ?? "null"} user={tp?.Name ?? "?"}");
                }
            }

            var group = TShock.Players.Where(x => x is not null && x.Active).GroupBy(x => x.Name).Where(g => g.Count() > 1).ToArray();
            foreach (var g in group)
            {
                Warn($"{DefaultTag} 状态不一致: 同名玩家出现 {g.Count()} 次 -> {g.Key}（索引 {string.Join(",", g.Select(x => x.Index))}）");
            }
        }
        catch { }
    }

    // ==================== 返回路径（后进先出） ====================

    private void PushOrigin(int idx, ServerContext from)
    {
        List<ServerContext> stack = origins.GetOrAdd(idx, static _ => []);
        lock (stack) { stack.Add(from); }
    }

    private ServerContext? PeekOrigin(int idx)
    {
        if (!origins.TryGetValue(idx, out List<ServerContext>? stack)) { return null; }
        lock (stack) { return stack.Count == 0 ? null : stack[^1]; }
    }

    private void PopOrigin(int idx)
    {
        if (!origins.TryGetValue(idx, out List<ServerContext>? stack)) { return; }
        lock (stack)
        {
            if (stack.Count > 0) { stack.RemoveAt(stack.Count - 1); }
        }
    }

    private void ClearOrigins(int idx) => origins.TryRemove(idx, out _);

    // ==================== 诊断 ====================

    private static string Tag(VirtualWorldInstance? w) =>
        !string.IsNullOrEmpty(w?.Request.LogTag) ? w!.Request.LogTag : DefaultTag;

    private static void Diag(string tag, int idx, ServerContext? from, ServerContext to, VirtualWorldInstance? w)
    {
        if (!DiagnosticsEnabled) { return; }
        try
        {
            TSPlayer? tp = TShock.Players.ElementAtOrDefault(idx);
            bool aFrom = false, aTo = false;
            float x = 0f, y = 0f;
            try { if (from is not null && idx >= 0 && idx < from.Main.player.Length && from.Main.player[idx] is not null) { aFrom = from.Main.player[idx].active; } } catch { }
            try { if (idx >= 0 && idx < to.Main.player.Length && to.Main.player[idx] is not null) { aTo = to.Main.player[idx].active; x = to.Main.player[idx].position.X; y = to.Main.player[idx].position.Y; } } catch { }
            Info($"{Tag(w)} 转移诊断[{tag}] plr={idx} name={tp?.Name ?? "?"} tshock.Active={tp?.Active} from={from?.Name}(active={aFrom}) to={to.Name}(active={aTo} pos={x:F0},{y:F0})");
        }
        catch (Exception ex) { Warn($"{Tag(w)} 诊断失败: {ex.Message}"); }
    }

    private static void DiagDelayed(string tag, int idx, ServerContext to, VirtualWorldInstance? w)
    {
        if (!DiagnosticsEnabled) { return; }
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(4000);
                to.Dispatcher.Post(() =>
                {
                    try
                    {
                        TSPlayer? tp = TShock.Players.ElementAtOrDefault(idx);
                        ServerContext? cur = UnifiedServerCoordinator.GetClientCurrentlyServer(idx);
                        bool act = false;
                        float x = 0f, y = 0f;
                        if (idx >= 0 && idx < to.Main.player.Length && to.Main.player[idx] is not null)
                        {
                            act = to.Main.player[idx].active;
                            x = to.Main.player[idx].position.X;
                            y = to.Main.player[idx].position.Y;
                        }
                        Info($"{Tag(w)} 转移诊断[{tag}+4s] plr={idx} name={tp?.Name ?? "?"} tshock.Active={tp?.Active} worldActive={act} cur={cur?.Name ?? "null"} pos={x:F0},{y:F0}");
                    }
                    catch { }
                });
            }
            catch { }
        });
    }

    /// <summary>依次调用可否决的转移事件；任一监听方置 Cancel 就返回失败。</summary>
    private static VirtualWorldResult RaiseTransferring(Action<VirtualWorldTransferArgs>? handlers, VirtualWorldTransferArgs args)
    {
        if (handlers is null) { return VirtualWorldResult.Ok; }
        foreach (Action<VirtualWorldTransferArgs> h in handlers.GetInvocationList())
        {
            try { h(args); } catch { }
            if (args.Cancel)
            {
                return VirtualWorldResult.Fail(VirtualWorldFailure.EventCanceled, args.CancelReason ?? "被事件监听方否决");
            }
        }
        return VirtualWorldResult.Ok;
    }

    private static void SafeEntered(IVirtualWorldPolicy? pol, TSPlayer p, IVirtualWorld w)
    {
        try { pol?.OnPlayerEntered(p, w); } catch { }
    }

    private static void SafeLeft(IVirtualWorldPolicy? pol, TSPlayer p, IVirtualWorld w)
    {
        try { pol?.OnPlayerLeft(p, w); } catch { }
    }

    private static void Info(string msg) { try { TShock.Log.Info(msg); } catch { } }
    private static void Warn(string msg) { try { TShock.Log.Warning(msg); } catch { } }
    private static void Error(string msg) { try { TShock.Log.Error(msg); } catch { } }

    internal static string ShortKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (char c in key) { if (char.IsLetterOrDigit(c)) { sb.Append(c); } }
        string k = sb.ToString();
        return k.Length > 12 ? k[..12] : k;
    }
}

/// <summary>不建控制台窗口的 ServerContext（与改造前 PocketWorld.PotServer 等价）。</summary>
internal sealed class HeadlessWorldServer(string name, IWorldDataProvider data) : ServerContext(name, data)
{
    public override bool SupportsConsoleInput => false;
    protected override ServerSurfaceConsole CreateSurfaceConsole() => new HeadlessServerSurfaceConsole(this);
}
