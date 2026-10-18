using System.Collections.Concurrent;
using System.Collections.Immutable;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;
using WorldChainSync.Core;

namespace WorldChainSync;

/// <summary>
/// 世界链状态同步（同步类插件）。
///
/// 与 EdgeStitch 的分工：
///   · EdgeStitch = 换乘（拓展）：触发、落点、冷却、链配置
///   · 本插件     = 同步：时间/天气/世界进度在【世界链】内保持一致
///
/// 两条同步路径：
///   1. 换乘瞬时：每 250ms 检测玩家所在世界是否变化，变了就把旧世界状态拷给新世界；
///   2. 周期统一：每 2s 以"有玩家的世界"为权威，推给链上其它世界（否则各世界时间会漂移）。
/// 玩家自身的 buff/血量/背包不需要同步（UTSL 换乘迁移的是同一个 Player 对象）。
/// </summary>
[PluginMetadata("WorldChainSync", "1.3.0", "星梦",
    "世界链状态同步：时间/天气/Boss 进度在链内世界保持一致，换乘瞬间同步")]
public sealed class WorldChainSyncPlugin : BasePlugin
{
    private static WorldChainSyncConfig _config = new();
    private static Timer? _timer;
    private static readonly ConcurrentDictionary<int, ServerContext> _lastWorld = new();
    private static long _lastUnify;

    /// <summary>排在 TShock 之后。</summary>
    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock") { await initInfo.InitializationTask; }
        }

        string path = Path.Combine(configRegistrar.Directory, "WorldChainSync.json");
        _config = WorldChainSyncConfig.Load(path);

        int tick = Math.Clamp(_config.DetectIntervalMs, 100, 5000);
        _timer = new Timer(_ => Tick(), null, tick, tick);
        TShock.Log.Info($"[WorldChainSync] 已加载，世界链: {string.Join(" — ", _config.WorldChain)}"
            + $"（检测 {tick}ms，统一 {Math.Max(500, _config.UnifyIntervalMs)}ms）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { try { _timer?.Dispose(); } catch { } }
        return ValueTask.CompletedTask;
    }

    private static void Tick()
    {
        try
        {
            List<ServerContext> chain = ResolveChain();
            if (chain.Count == 0) { return; }

            // 1) 换乘瞬时同步
            for (int i = 0; i < 256; i++)
            {
                TSPlayer? tp = TShock.Players.ElementAtOrDefault(i);
                if (tp is null || !tp.Active) { _lastWorld.TryRemove(i, out _); continue; }
                ServerContext? cur = UnifiedServerCoordinator.GetClientCurrentlyServer(i);
                if (cur is null) { continue; }

                if (_lastWorld.TryGetValue(i, out ServerContext? prev) && !ReferenceEquals(prev, cur)
                    && chain.Exists(w => ReferenceEquals(w, prev)) && chain.Exists(w => ReferenceEquals(w, cur)))
                {
                    SyncTo(prev, cur, log: true);
                }
                _lastWorld[i] = cur;
            }

            // 2) 周期统一
            long now = Environment.TickCount64;
            if (now - _lastUnify < Math.Max(500, _config.UnifyIntervalMs)) { return; }
            _lastUnify = now;

            // 没人在线时世界不 tick（时间冻结），不需要周期性统一；跳过可省掉每 2 秒的空写。
            ServerContext? master = null;
            foreach (ServerContext w in chain)
            {
                if (w.Main.player.Any(p => p is not null && p.active)) { master = w; break; }
            }
            if (master is null) { return; }

            foreach (ServerContext w in chain)
            {
                if (!ReferenceEquals(w, master)) { SyncTo(master, w, log: false); }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[WorldChainSync] 同步异常: " + ex.Message); }
    }

    /// <summary>把 from 的世界级状态写到 to 的更新线程上（跨线程直接改别人的 Main 不安全）。</summary>
    private static void SyncTo(ServerContext from, ServerContext to, bool log)
    {
        try
        {
            if (to.Dispatcher.CheckAccess()) { WorldStateSync.CopyWorldState(from, to, _config.SyncContent, log); return; }
            to.Dispatcher.Post(() =>
            {
                try { WorldStateSync.CopyWorldState(from, to, _config.SyncContent, log); }
                catch (Exception ex) { TShock.Log.Warning("[WorldChainSync] 写入失败: " + ex.Message); }
            });
        }
        catch (Exception ex) { TShock.Log.Warning("[WorldChainSync] 投递失败: " + ex.Message); }
    }

    private static List<ServerContext> ResolveChain()
    {
        List<ServerContext> list = [];
        foreach (string name in _config.WorldChain)
        {
            ServerContext? w = UnifiedServerCoordinator.Servers.FirstOrDefault(
                x => x.IsRunning && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (w is not null) { list.Add(w); }
        }
        return list;
    }
}
