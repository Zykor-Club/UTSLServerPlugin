using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using UnifierTSL;
using UnifierTSL.Servers;

namespace UTSL.VirtualWorld;

/// <summary>
/// 【临时·自检】区块建筑模板（.utpl）在真实 ServerContext 上的端到端自检。用完即删。
/// 只在隔离的假世界里动手，绝不碰主世界。
/// </summary>
internal static class UtplSelfTest
{
    private const int SaveX = 300, SaveY = 300, SaveW = 40, SaveH = 30;
    private const int PasteX = 420, PasteY = 640;

    public static async Task<string> RunAsync(VirtualWorldService svc, string seedWorldPath, Action<string> log, CancellationToken ct)
    {
        StringBuilder sb = new();
        int pass = 0, fail = 0;
        void Check(bool ok, string what)
        {
            if (ok) { pass++; } else { fail++; }
            sb.AppendLine((ok ? "PASS " : "FAIL ") + what);
        }
        void Note(string s) => sb.AppendLine("     " + s);

        TaskCompletionSource<T> OnWorld<T>(ServerContext ctx, Func<T> f)
        {
            TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Dispatcher.Post(() =>
            {
                try { tcs.TrySetResult(f()); }
                catch (Exception e) { tcs.TrySetException(e); }
            });
            return tcs;
        }

        VirtualWorldInstance? w = null;
        ServerContext? ctx = null;

        try
        {
            // ---- 0) 隔离世界 ----
            VirtualWorldRequest req = new()
            {
                Key = "itest:utpl",
                DisplayName = "UTPL自检",
                OwnerKey = "itest:utpl",
                Group = "MyPotWorld",
                ServerName = "UTPL_Verify",
                WorldFileName = "UTSL_UTPL_VERIFY",
                TilesX = 4200,
                TilesY = 1200,
                SeedWorldPath = seedWorldPath,
                Headless = true,
                LogTag = "[UTPL自检]",
            };
            w = (VirtualWorldInstance)await svc.EnsureAsync(req, ct).ConfigureAwait(false);
            ctx = w.Context;
            if (ctx is null) { sb.AppendLine("FATAL 世界未加载"); return sb.ToString(); }

            await OnWorld(ctx, () => { ctx.ClearCurrentWorld(); return true; }).Task.WaitAsync(ct).ConfigureAwait(false);
            Note($"隔离世界就绪: {ctx.Name} {ctx.Main.maxTilesX}x{ctx.Main.maxTilesY}");

            // ---- 1) 铺内容 + 记录期望 ----
            List<(int lx, int ly, TileData t)> expected = [];
            List<(int x, int y, string name, List<(int type, int stack, int prefix)> items)> expChests = [];
            List<(int x, int y, string text)> expSigns = [];
            List<(byte kind, int x, int y, int slotCount, bool on)> expEnts = [];

            string buildErr = await OnWorld(ctx, () =>
            {
                try
                {
                    BuildContent(ctx, expected, expChests, expSigns);
                    return "";
                }
                catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
            }).Task.WaitAsync(ct).ConfigureAwait(false);
            Check(buildErr.Length == 0, $"搭建测试场景成功{(buildErr.Length == 0 ? "" : " -> " + buildErr)}");
            if (buildErr.Length > 0) { return sb.ToString(); }

            Note($"源矩形: 非空格子={expected.Count} 箱子={expChests.Count} 标牌={expSigns.Count}");

            // ---- 2) SaveRegionAsync ----
            string tpl = "itest_house";
            VirtualWorldResult saveRes = await svc.SaveRegionAsync(w, tpl, SaveX, SaveY, SaveW, SaveH, ct).ConfigureAwait(false);
            Check(saveRes.Success, $"SaveRegionAsync（{saveRes}）");
            if (!saveRes.Success) { return sb.ToString(); }

            string tplFile = Path.Combine(VirtualWorldService.TemplatesDirForTest() ?? "", tpl + ".utpl");
            Check(File.Exists(tplFile), $"模板文件已生成（{new FileInfo(tplFile).Length} 字节）");

            VirtualWorldTemplateInfo? info = svc.FindRegionTemplate(tpl);
            Check(info is not null && info.Value.Width == SaveW && info.Value.Height == SaveH,
                $"FindRegionTemplate 尺寸 = {info?.Width}x{info?.Height}（期望 {SaveW}x{SaveH}）");

            // 读回模板，把"期望"与"文件内容"对齐（这才是捕获阶段真正的断言）
            UtplRegion reg = svc.ReadTemplateForTest(tplFile);
            int capMismatch = 0;
            foreach ((int lx, int ly, TileData want) in expected)
            {
                if (!SameTile(reg.At(lx, ly), want)) { capMismatch++; }
            }
            Check(capMismatch == 0, $"捕获：模板图格与源世界一致（{expected.Count} 格，不一致 {capMismatch}）");
            Check(reg.Chests.Count == expChests.Count, $"捕获：箱子数 = {reg.Chests.Count}（期望 {expChests.Count}）");
            Check(reg.Signs.Count == expSigns.Count, $"捕获：标牌数 = {reg.Signs.Count}（期望 {expSigns.Count}）");
            if (reg.Chests.Count > 0 && expChests.Count > 0)
            {
                Check(reg.Chests[0].X == expChests[0].x - SaveX && reg.Chests[0].Y == expChests[0].y - SaveY,
                    $"捕获：箱子相对坐标 = ({reg.Chests[0].X},{reg.Chests[0].Y})（期望 ({expChests[0].x - SaveX},{expChests[0].y - SaveY})）");
            }
            if (reg.Signs.Count > 0 && expSigns.Count > 0)
            {
                Check(reg.Signs[0].X == expSigns[0].x - SaveX && reg.Signs[0].Y == expSigns[0].y - SaveY,
                    $"捕获：标牌相对坐标 = ({reg.Signs[0].X},{reg.Signs[0].Y})（期望 ({expSigns[0].x - SaveX},{expSigns[0].y - SaveY})）");
            }
            Check(reg.Entities.Count == 2, $"捕获：实体数 = {reg.Entities.Count}（期望 2 = 物品框 + 逻辑感应器）");
            foreach ((byte kind, int x, int y, int slotCount, bool on) in expEnts)
            {
                UtplEntity? got = reg.Entities.FirstOrDefault(e => e.X == x && e.Y == y);
                Check(got is not null && got.Kind == kind && got.Slots.Count == slotCount && got.On == on,
                    $"捕获：实体 kind={kind} @({x},{y}) slots={slotCount} on={on}");
            }

            // ---- 3) 清空源矩形 ----
            await OnWorld(ctx, () =>
            {
                for (int y = SaveY; y < SaveY + SaveH; y++)
                    for (int x = SaveX; x < SaveX + SaveW; x++) { ctx.TileEntity.Kill(x, y, -1); ctx.Main.tile[x, y].ClearEverything(); }
                foreach (Chest c in ctx.Main.chest)
                {
                    if (c is null || c.x < SaveX || c.x >= SaveX + SaveW || c.y < SaveY || c.y >= SaveY + SaveH) { continue; }
                    ctx.Chest.DestroyChest(c.x, c.y);
                }
                foreach (Sign s in ctx.Main.sign)
                {
                    if (s is null || s.x < SaveX || s.x >= SaveX + SaveW || s.y < SaveY || s.y >= SaveY + SaveH) { continue; }
                    Sign.KillSign(ctx, s.x, s.y);
                }
                return true;
            }).Task.WaitAsync(ct).ConfigureAwait(false);

            // ---- 4) 贴到别处 ----
            VirtualWorldResult pasteRes = await svc.ApplyTemplateAsync(w, tpl, PasteX, PasteY, ct).ConfigureAwait(false);
            Check(pasteRes.Success, $"ApplyTemplateAsync（{pasteRes}）");
            if (!pasteRes.Success) { return sb.ToString(); }

            // ---- 5) 图格逐格 ----
            (int mism, string mimsg) = await OnWorld(ctx, () =>
            {
                int bad = 0; StringBuilder m = new();
                foreach ((int lx, int ly, TileData want) in expected)
                {
                    TileData got = ctx.Main.tile[PasteX + lx, PasteY + ly];
                    if (!SameTile(got, want))
                    {
                        bad++;
                        if (bad <= 5) { m.Append($" 局部({lx},{ly}) 期望[type={want.type} a={want.active()} wall={want.wall} f=({want.frameX},{want.frameY})] 实得[type={got.type} a={got.active()} wall={got.wall} f=({got.frameX},{got.frameY})]"); }
                    }
                }
                return (bad, m.ToString());
            }).Task.WaitAsync(ct).ConfigureAwait(false);
            Check(mism == 0, $"粘贴：图格逐格一致（{expected.Count} 格，不一致 {mism}）{mimsg}");

            // ---- 6) 箱子 ----
            (int cOk, int cBad, string cMsg) = await OnWorld(ctx, () =>
            {
                int ok = 0, bad = 0; string msg = "";
                foreach ((int x, int y, string name, List<(int type, int stack, int prefix)> items) in expChests)
                {
                    int px = PasteX + (x - SaveX), py = PasteY + (y - SaveY);   // expChests 存的是源世界绝对坐标
                    int idx = ctx.Chest.FindChest(px, py);
                    if (idx < 0) { bad++; msg += $" 缺({px},{py})"; continue; }
                    Chest? c = ctx.Main.chest[idx];
                    if (c is null || (c.name ?? "") != name || c.item is null) { bad++; msg += $" 名字不符({px},{py})='{c?.name}'"; continue; }
                    bool allFound = true;
                    foreach ((int type, int stack, int prefix) in items)
                    {
                        bool f = c.item.Any(it => it is not null && it.type == type && it.stack == stack && it.prefix == (byte)prefix);
                        if (!f) { allFound = false; msg += $" 缺物品{type}x{stack}"; }
                    }
                    if (allFound) { ok++; } else { bad++; }
                }
                return (ok, bad, msg);
            }).Task.WaitAsync(ct).ConfigureAwait(false);
            Check(cBad == 0 && cOk == expChests.Count, $"粘贴：箱子内容一致（{cOk}/{expChests.Count}）{cMsg}");

            // ---- 7) 标牌 ----
            (int sOk, int sBad, string sMsg) = await OnWorld(ctx, () =>
            {
                int ok = 0, bad = 0; string msg = "";
                foreach ((int x, int y, string text) in expSigns)
                {
                    // ★ 标牌贴回后 y 会 -1（PlaceSign 把牌格摆在下、Sign 对象登记在上一格），实测确认
                    int px = PasteX + (x - SaveX), py = PasteY + (y - SaveY);
                    int id = Sign.ReadSign(ctx, px, py, false);
                    if (id < 0) { bad++; msg += $" 缺({px},{py}) active={ctx.Main.tile[px, py].active()} type={ctx.Main.tile[px, py].type} 全部牌=" + string.Join("|", ctx.Main.sign.Where(static s => s is not null).Select(static s => $"({s.x},{s.y})")); continue; }
                    string got = ctx.Main.sign[id].text ?? "";
                    if (got == text) { ok++; } else { bad++; msg += $" 文字不符('{got.Replace("\n", "\\n")}')"; }
                }
                return (ok, bad, msg);
            }).Task.WaitAsync(ct).ConfigureAwait(false);
            Check(sBad == 0 && sOk == expSigns.Count, $"粘贴：标牌内容一致（{sOk}/{expSigns.Count}）{sMsg}");

            // ---- 8) 实体 ----
            (int eOk, int eBad, string eMsg) = await OnWorld(ctx, () =>
            {
                int ok = 0, bad = 0; string msg = "";
                foreach (UtplEntity ue in reg.Entities)
                {
                    int px = PasteX + ue.X, py = PasteY + ue.Y;
                    if (!ctx.TileEntity.TryGetAt(px, py, out TileEntity got) || got is null) { bad++; msg += $" 缺 kind={ue.Kind}({px},{py})"; continue; }
                    if (ue.Kind == UtplFormat.KindLogicSensor)
                    {
                        if (got is TELogicSensor ls && ls.On == ue.On) { ok++; } else { bad++; msg += $" 感应器 on 不符({px},{py})"; }
                    }
                    else if (got.GetType().Name.Contains("ItemFrame") || got.GetType().Name.Contains("WeaponsRack"))
                    {
                        ok++;
                    }
                    else { bad++; msg += $" 类型不符 {got.GetType().Name}"; }
                }
                return (ok, bad, msg);
            }).Task.WaitAsync(ct).ConfigureAwait(false);
            Check(eBad == 0 && eOk == reg.Entities.Count, $"粘贴：实体一致（{eOk}/{reg.Entities.Count}）{eMsg}");

            // ---- 9) 越界裁剪 ----
            VirtualWorldResult edge = await svc.ApplyTemplateAsync(w, tpl, ctx.Main.maxTilesX - 5, ctx.Main.maxTilesY - 5, ct).ConfigureAwait(false);
            Check(edge.Success, $"越界粘贴被安全裁剪（{edge}）");

            bool srcEmpty = await OnWorld(ctx, () =>
            {
                for (int y = SaveY; y < SaveY + SaveH; y++)
                    for (int x = SaveX; x < SaveX + SaveW; x++)
                        if (!UtplFormat.IsEmpty(ctx.Main.tile[x, y])) { return false; }
                return true;
            }).Task.WaitAsync(ct).ConfigureAwait(false);
            Check(srcEmpty, "源矩形仍为空（粘贴没回头污染源区域）");

            // ---- 10) 坏输入 ----
            VirtualWorldResult missing = await svc.ApplyTemplateAsync(w, "itest_不存在", PasteX, PasteY, ct).ConfigureAwait(false);
            Check(!missing.Success && missing.Reason == VirtualWorldFailure.PolicyRejected, $"不存在的模板 -> PolicyRejected（{missing.Reason}）");

            string badFile = Path.Combine(VirtualWorldService.TemplatesDirForTest() ?? "", "itest_bad.utpl");
            File.WriteAllText(badFile, "not a utpl");
            VirtualWorldResult bad = await svc.ApplyTemplateAsync(w, "itest_bad", PasteX, PasteY, ct).ConfigureAwait(false);
            Check(!bad.Success, $"损坏模板 -> 失败不崩溃（{bad.Reason}）");
            try { File.Delete(badFile); } catch { }

            string? tdir = VirtualWorldService.TemplatesDirForTest();
            VirtualWorldResult evil = await svc.SaveRegionAsync(w, "../../evil", SaveX, SaveY, 4, 4, ct).ConfigureAwait(false);
            string escaped = Path.GetFullPath(Path.Combine(tdir ?? "", "..", "..", "evil.utpl"));
            Check(evil.Success && !File.Exists(escaped), "路径穿越名被净化（没写到模板目录外）");
            if (evil.Success) { try { File.Delete(Path.Combine(tdir ?? "", ".._.._evil.utpl")); } catch { } }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("FAIL 自检异常: " + ex);
        }
        finally
        {
            try
            {
                if (w is not null)
                {
                    VirtualWorldResult d = await svc.DestroyAsync(w, save: false, ct).ConfigureAwait(false);
                    sb.AppendLine("     清理隔离世界: " + d);
                }
            }
            catch (Exception e) { sb.AppendLine("     清理失败: " + e.Message); }
        }

        sb.Insert(0, $"===== UTPL 自检结果: PASS={pass} FAIL={fail} =====\n");
        log(sb.ToString());
        return sb.ToString();
    }

    /// <summary>在世界线程上铺一个【稀疏、真实感】的场景，并在最后按【最终世界状态】整片快照成期望值。</summary>
    private static void BuildContent(ServerContext ctx,
        List<(int lx, int ly, TileData t)> expected,
        List<(int x, int y, string name, List<(int type, int stack, int prefix)> items)> expChests,
        List<(int x, int y, string text)> expSigns)
    {
        // 建筑外壳：地板（石板+反色漆）/ 左右墙 + 屋顶（木材）
        for (int x = 0; x < SaveW; x++)
        {
            PlaceSolid(ctx, SaveX + x, SaveY + SaveH - 1, 273);
            ctx.Main.tile[SaveX + x, SaveY + SaveH - 1].color(30);
            PlaceSolid(ctx, SaveX + x, SaveY, 30);
        }
        for (int y = 0; y < SaveH; y++)
        {
            PlaceSolid(ctx, SaveX, SaveY + y, 30);
            PlaceSolid(ctx, SaveX + SaveW - 1, SaveY + y, 30);
        }

        // 家具（frame-important）
        PlaceSolid(ctx, SaveX + 14, SaveY + 26, 32);
        PlaceSolid(ctx, SaveX + 16, SaveY + 26, 34);
        PlaceSolid(ctx, SaveX + 18, SaveY + 24, 4);
        ctx.Main.tile[SaveX + 18, SaveY + 24].color(13);

        // 全字段：斜坡/半砖/电线/液体
        {
            ref TileData slope = ref ctx.Main.tile[SaveX + 30, SaveY + SaveH - 2];
            slope.active(true); slope.type = 3; slope.slope(2); slope.wire(true); slope.liquid = 120; slope.liquidType(1);

            ref TileData half = ref ctx.Main.tile[SaveX + 33, SaveY + SaveH - 2];
            half.active(true); half.type = 3; half.halfBrick(true); half.actuator(true); half.wire2(true);
        }

        // 箱子（实时查落点，别假设）
        int wantChestX = SaveX + 8, wantChestY = SaveY + 26;
        ctx.Chest.CreateChest(wantChestX, wantChestY);
        int foundX = -1, foundY = -1;
        for (int r = 0; r <= 2 && foundX < 0; r++)
        {
            for (int dy = -r; dy <= r && foundX < 0; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int fx = wantChestX + dx, fy = wantChestY + dy;
                    if (ctx.Chest.FindChest(fx, fy) >= 0) { foundX = fx; foundY = fy; break; }
                }
        }
        if (foundX < 0) { throw new InvalidOperationException("CreateChest 之后找不到箱子落点"); }
        int ci = ctx.Chest.FindChest(foundX, foundY);
        Chest? chest = ci >= 0 && ci < ctx.Main.chest.Length ? ctx.Main.chest[ci] : null;
        if (chest is null) { throw new InvalidOperationException("拿不到箱子对象"); }
        List<(int type, int stack, int prefix)> chestItems = [(8, 7, 0), (48, 3, 0), (75, 199, 2)];
        chest.name = "自检箱子";
        if (chest.item is null || chest.item.Length < 40) { try { chest.Resize(40); } catch { } }
        if (chest.item is null) { throw new InvalidOperationException("箱子没有物品数组"); }
        for (int i = 0; i < chestItems.Count && i < chest.item.Length; i++)
        {
            chest.item[i] = new Item();
            chest.item[i].SetDefaults(ctx, chestItems[i].type);
            chest.item[i].stack = chestItems[i].stack;
            chest.item[i].prefix = (byte)chestItems[i].prefix;
        }
        expChests.Add((foundX, foundY, "自检箱子", chestItems));

        // 标牌：交给 PlaceSign 自己摆（frame-important）
        {
            int sx = SaveX + 22, sy = SaveY + SaveH - 2;
            ClearTile(ctx, sx, sy);
            if (!ctx.WorldGen.PlaceSign(sx, sy, 55, 0)) { throw new InvalidOperationException($"PlaceSign({sx},{sy}) 失败"); }
            int id = Sign.ReadSign(ctx, sx, sy, true);
            if (id < 0) { throw new InvalidOperationException("ReadSign 拿不到槽位"); }
            const string text = "自检标牌\n第二行 中文";
            ctx.Main.sign[id].text = text;
            // ★ 以 Sign 对象自己的位置为准（实测它登记在牌格【上方一格】，不是 PlaceSign 传入的坐标）
            expSigns.Add((ctx.Main.sign[id].x, ctx.Main.sign[id].y, text));
        }

        // 物品框（TileEntity.Place 返回 0 但确实放上了 -> 用 ByPosition 判定）
        {
            int fx = SaveX + 26, fy = SaveY + SaveH - 2;
            ClearTile(ctx, fx, fy);
            PlaceSolid(ctx, fx, fy, 395);
            ctx.TileEntity.Place(fx, fy, 1);
            if (!ctx.TileEntity.TryGetAt(fx, fy, out TEItemFrame frame) || frame is null) { throw new InvalidOperationException($"物品框没放上 ({fx},{fy})"); }
            frame.item = new Item();
            frame.item.SetDefaults(ctx, 8);
            frame.item.stack = 7;
        }

        // 逻辑感应器
        {
            int lx = SaveX + 28, ly = SaveY + SaveH - 2;
            ClearTile(ctx, lx, ly);
            PlaceSolid(ctx, lx, ly, 423);
            ctx.TileEntity.Place(lx, ly, 2);
            if (!ctx.TileEntity.TryGetAt(lx, ly, out TELogicSensor ls) || ls is null) { throw new InvalidOperationException($"感应器没放上 ({lx},{ly})"); }
            ls.On = true;
        }

        // ★ 最后：按【世界最终状态】整片快照（前面的 CreateChest/PlaceSign 会改图格）
        for (int ly = 0; ly < SaveH; ly++)
        {
            for (int lx = 0; lx < SaveW; lx++)
            {
                expected.Add((lx, ly, ctx.Main.tile[SaveX + lx, SaveY + ly]));
            }
        }
    }

    private static void PlaceSolid(ServerContext ctx, int x, int y, ushort type)
    {
        ref TileData t = ref ctx.Main.tile[x, y];
        t.active(true);
        t.type = type;
    }

    private static void ClearTile(ServerContext ctx, int x, int y)
    {
        ctx.TileEntity.Kill(x, y, -1);
        ctx.Main.tile[x, y].ClearEverything();
    }

    private static bool SameTile(TileData x, TileData y) =>
        x.type == y.type && x.wall == y.wall && x.sTileHeader == y.sTileHeader && x.frameX == y.frameX &&
        x.frameY == y.frameY && x.liquid == y.liquid && x.bTileHeader == y.bTileHeader &&
        x.bTileHeader2 == y.bTileHeader2 && x.bTileHeader3 == y.bTileHeader3;
}
