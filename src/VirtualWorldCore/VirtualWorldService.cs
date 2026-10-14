using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
internal sealed class VirtualWorldService : IVirtualWorldCore
{
    /// <summary>诊断日志总开关（默认开，与改造前 PocketWorld 的 TransferDiagOn 一致）。</summary>
    internal static bool DiagnosticsEnabled = true;

    private readonly ConcurrentDictionary<string, VirtualWorldInstance> worlds = new(StringComparer.Ordinal);

    /// <summary>每玩家的"返回路径"（后进先出）。Enter 时压入来源世界，Leave 时弹出。</summary>
    private readonly ConcurrentDictionary<int, List<ServerContext>> origins = new();

    /// <summary>最后一次使用的日志前缀（无请求上下文时的兜底）。</summary>
    private const string DefaultTag = "[VirtualWorldCore]";

    // ==================== 注册表 ====================

    public IReadOnlyList<IVirtualWorld> Worlds => worlds.Values.Cast<IVirtualWorld>().ToArray();

    public IVirtualWorld? Find(string key) =>
        !string.IsNullOrEmpty(key) && worlds.TryGetValue(key, out VirtualWorldInstance? w) ? w : null;

    public IVirtualWorld? FindByOwner(string ownerKey) =>
        string.IsNullOrEmpty(ownerKey)
            ? null
            : worlds.Values.FirstOrDefault(w => string.Equals(w.OwnerKey, ownerKey, StringComparison.Ordinal));

    internal VirtualWorldInstance? FindByContext(ServerContext? ctx) =>
        ctx is null ? null : worlds.Values.FirstOrDefault(w => ReferenceEquals(w.Context, ctx));

    internal bool Remove(string key) => worlds.TryRemove(key, out _);

    public event Action<IVirtualWorld>? WorldLoaded;
    public event Action<IVirtualWorld>? WorldDestroyed;
    public event Action<TSPlayer, IVirtualWorld>? PlayerEntered;
    public event Action<TSPlayer, IVirtualWorld>? PlayerLeft;

    // ==================== 生命周期：创建 / 加载 ====================

    public Task<IVirtualWorld> EnsureAsync(VirtualWorldRequest request, CancellationToken ct) =>
        Task.Run(() => EnsureCore(request), ct);

    private IVirtualWorld EnsureCore(VirtualWorldRequest request)
    {
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
                    if (string.IsNullOrEmpty(request.SeedWorldPath))
                    {
                        throw new InvalidOperationException($"世界文件不存在且未提供种子: {worldFile}");
                    }
                    if (!File.Exists(request.SeedWorldPath))
                    {
                        throw new FileNotFoundException($"种子世界文件不存在: {request.SeedWorldPath}");
                    }

                    string? dir = Path.GetDirectoryName(worldFile);
                    if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

                    try
                    {
                        File.Copy(request.SeedWorldPath, worldFile, overwrite: true);
                    }
                    catch (Exception exCopy)
                    {
                        // File.Copy 在部分路径/占用情形下会抛 UnauthorizedAccessException，流式复制可绕过
                        Warn($"{Tag(w)} File.Copy 失败({exCopy.GetType().Name})，改用流式复制");
                        using FileStream src = File.OpenRead(request.SeedWorldPath);
                        using FileStream dst = File.Create(worldFile);
                        src.CopyTo(dst);
                    }
                    seededNow = true;
                    Info($"{Tag(w)} 已用种子世界文件建立 {worldFile}");
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
            while (waited < 120000 && !ctx.IsRunning) { Thread.Sleep(500); waited += 500; }
            if (!ctx.IsRunning)
            {
                // 启动超时：不但不登记，还要把已创建的世界收掉，避免留下跑着的孤儿世界
                w.Context = null;
                Remove(w.Key);
                try { ctx.Close().Wait(TimeSpan.FromSeconds(10)); } catch { }
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
                if (!loaded.Task.Wait(TimeSpan.FromSeconds(30)))
                {
                    Warn($"{Tag(w)} OnLoaded 等待超时（世界可能空转，Post 队列需空闲驱动）");
                }
            }

            Info($"{Tag(w)} 世界已加载: {ctx.Name}（{request.TilesX}x{request.TilesY}，刚播种={seededNow}）");
            WorldLoaded?.Invoke(w);
        }

        return w;
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

        var done = new TaskCompletionSource<VirtualWorldResult>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                                // 真人不会走到这里：上方"世界玩家表"守卫已保证世界内无 active 玩家
                                continue;
                            }
                            if (TShock.Players[ci] is not null) { TShock.Players[ci] = null; }
                            Warn($"{Tag(w)} 已清理悬空玩家条目 #{ci}（卸载 {w.DisplayName} 前）");
                        }
                        catch { }
                    }

                    try { ctx.Close().Wait(TimeSpan.FromSeconds(60)); } catch (Exception ex) { Warn($"{Tag(w)} Close 异常: {ex.Message}"); }
                    try { UnifiedServerCoordinator.RemoveServer(ctx); } catch (Exception ex) { Warn($"{Tag(w)} RemoveServer 异常: {ex.Message}"); }

                    w.Context = null;
                    Remove(w.Key);
                    Info($"{Tag(w)} {w.DisplayName} 已成功卸载。");
                    WorldDestroyed?.Invoke(w);
                    done.TrySetResult(VirtualWorldResult.Ok);
                }
                catch (Exception ex)
                {
                    Error($"{Tag(w)} 卸载失败: {ex}");
                    done.TrySetResult(VirtualWorldResult.Fail(VirtualWorldFailure.Exception, ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            Error($"{Tag(w)} 卸载投递失败: {ex.Message}");
            return Task.FromResult(VirtualWorldResult.Fail(VirtualWorldFailure.NotRunning, ex.Message));
        }
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
