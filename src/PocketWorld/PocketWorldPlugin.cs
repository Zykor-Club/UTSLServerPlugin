using System.Collections.Concurrent;
using System.Collections.Immutable;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Plugins;
using UnifierTSL.Surface.Hosting.Server;
using UnifierTSL.Servers;

namespace PocketWorld;

/// <summary>
/// 尘歌壶 —— 每个玩家一个隔离的私人世界。
///
/// 设计（v0.3.0）：
///   · 按需创建：玩家第一次 /pocket 时建立属于他的壶；
///   · 闲置销毁：壶里没人超过「闲置销毁_秒」→ 存档 + Close + RemoveServer（真正销毁）；
///   · 世界名 = 「玩家名 + 的尘歌壶」（日志可读性好）；世界文件按 UUID 命名 → 重建后仍是他的世界。
///
/// ★ 能这样做的前提（本项目 P1 成果）：UTSL 侧已修复"世界关不掉"的内存泄漏
///   （静态 Gameplay 组件 / ConditionalDialogue / AppContext / 输入线程 / ArmorSetBonuses / MidiPlayer 绑定），
///   实测 12 个世界只涨 1.1MB → 销毁/重建是安全的。
/// </summary>
[PluginMetadata("PocketWorld", "0.3.0", "星梦", "尘歌壶：每个玩家一个隔离的私人世界（按需创建、闲置销毁）")]
public sealed class PocketWorldPlugin : BasePlugin
{
    private static PocketWorldConfig _config = new();
    private static string _configPath = "";
    private static int _inited;
    private static Timer? _idleTimer;

    private sealed class Pot
    {
        public string Key = "";
        public string OwnerName = "";
        public string WorldName = "";
        public ServerContext? Server;
        public bool Busy;
        public long LastActiveMs;
        public int InsideIndex = -1;
    }

    private static readonly ConcurrentDictionary<string, Pot> Pots = new();
    private static readonly ConcurrentDictionary<int, Pot> Inside = new();
    private static readonly ConcurrentDictionary<int, ServerContext> ReturnTo = new();

    private static long NowMs => Environment.TickCount64;

    /// <summary>壶世界：不建控制台输入线程（否则会永久阻塞或空转吃满 CPU，见 UTSL 开发历史 §13）。</summary>
    private sealed class PotServer(string name, IWorldDataProvider data) : ServerContext(name, data)
    {
        public override bool SupportsConsoleInput => false;

        // 壶世界不需要控制台窗口：每个窗口会额外拉起一个 ConsoleClient 进程（实测约 26MB 私有 / 48MB 工作集）
        protected override ServerSurfaceConsole CreateSurfaceConsole() => new HeadlessServerSurfaceConsole(this);
    }

    public override Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        try { _configPath = Path.Combine(configRegistrar.Directory, "settings.json"); } catch { _configPath = ""; }
        _config = PocketWorldConfig.Load(_configPath);

        if (Interlocked.Exchange(ref _inited, 1) == 1) { return Task.CompletedTask; }

        Commands.ChatCommands.Add(new Command(_config.EnterPermission, Command, _config.CommandName, "cg"));
        PlayerHooks.PlayerLogout += OnLogout;

        if (_config.IdleDestroySeconds > 0)
        {
            _idleTimer = new Timer(_ => IdleTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        TShock.Log.Info($"[尘歌壶] 已加载（上限={_config.MaxPots}，闲置销毁={_config.IdleDestroySeconds}s，指令=/{_config.CommandName}）");
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            try { PlayerHooks.PlayerLogout -= OnLogout; } catch { }
            try { _idleTimer?.Dispose(); } catch { }
        }
        return ValueTask.CompletedTask;
    }

    private static void Command(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "exit":
            case "leave":
                Leave(args);
                break;
            case "status":
                Status(args);
                break;
            default:
                Enter(args);
                break;
        }
    }

    private static void Status(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        p?.SendInfoMessage($"[尘歌壶] 当前 {Pots.Count}/{_config.MaxPots} 个壶：");
        foreach (Pot pot in Pots.Values)
        {
            string who = pot.InsideIndex >= 0 ? "（有人在里面）" : "（空）";
            p?.SendInfoMessage($"  {pot.WorldName} {who}");
        }
        p?.SendInfoMessage($"[尘歌壶] 用法：/{_config.CommandName} 进入 · {_config.CommandName} exit 离开 · {_config.CommandName} status");
    }

    private static void Enter(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { TShock.Log.Info("[尘歌壶] 该指令需要玩家执行"); return; }

        string key = OwnerKey(p);
        ServerContext? from = UnifiedServerCoordinator.GetClientCurrentlyServer(p.Index);
        if (from is null) { p.SendErrorMessage("[尘歌壶] 找不到你当前所在的世界。"); return; }

        if (Inside.ContainsKey(p.Index))
        {
            p.SendInfoMessage("[尘歌壶] 你已经在自己的壶里了。用 /" + _config.CommandName + " exit 返回。");
            return;
        }

        Pots.TryGetValue(key, out Pot? pot);
        if (pot is not null && pot.Busy)
        {
            p.SendInfoMessage("[尘歌壶] 你的壶正在准备中，请稍候…");
            return;
        }
        if (pot is null)
        {
            if (Pots.Count >= Math.Max(1, _config.MaxPots))
            {
                p.SendErrorMessage($"[尘歌壶] 当前壶数已达上限（{_config.MaxPots}），请稍后再试。");
                return;
            }
            pot = new Pot { Key = key, OwnerName = p.Name, WorldName = MakeWorldName(p), Busy = true, LastActiveMs = NowMs };
            Pots[key] = pot;
        }
        else
        {
            pot.Busy = true;
            pot.LastActiveMs = NowMs;
        }

        int tileW = from.Main.maxTilesX, tileH = from.Main.maxTilesY;
        p.SendInfoMessage("[尘歌壶] 正在为你准备壶中世界…（首次进入需要加载，请稍候）");
        TShock.Log.Info($"[尘歌壶] {p.Name} 请求进入（世界={pot.WorldName}，{tileW}x{tileH}）");

        Task.Run(() =>
        {
            try
            {
                ServerContext ctx;
                if (pot.Server is null)
                {
                    ctx = new PotServer("Pot_" + ShortKey(key), BuildProvider(BootName(key), 200, 150));
                    try { ctx.Run([]); }
                    catch (Exception ex)
                    {
                        TShock.Log.Error("[尘歌壶] 建立世界失败: " + ex);
                        Pots.TryRemove(key, out _);
                        p.SendErrorMessage("[尘歌壶] 建立失败: " + ex.Message);
                        return;
                    }
                    UnifiedServerCoordinator.AddServer(ctx);
                    pot.Server = ctx;
                }
                else { ctx = pot.Server; }

                int waited = 0;
                while (waited < 120000 && !ctx.IsRunning) { Thread.Sleep(500); waited += 500; }
                if (!ctx.IsRunning) { pot.Busy = false; p.SendErrorMessage("[尘歌壶] 世界启动超时。"); return; }

                ServerContext target = ctx;
                target.Dispatcher.Post(() =>
                {
                    try
                    {
                        if (!string.Equals(target.WorldDataProvider.WorldName, WorldFileName(key), StringComparison.OrdinalIgnoreCase))
                        {
                            target.EnsureBlankWorld(WorldFileName(key), tileW, tileH);
                            target.SwitchWorld(BuildProvider(WorldFileName(key), tileW, tileH), saveCurrent: false, allowOnline: false);
                            target.BuildSpawnPlatform(20);
                        }
                        from.Dispatcher.Post(() =>
                        {
                            try
                            {
                                UnifiedServerCoordinator.TransferPlayerToServer((byte)p.Index, target);
                                ReturnTo[p.Index] = from;
                                pot.InsideIndex = p.Index;
                                pot.Busy = false;
                                pot.LastActiveMs = NowMs;
                                Inside[p.Index] = pot;
                                p.SendSuccessMessage($"[尘歌壶] 已进入你的壶中世界（{pot.WorldName}）。用 /{_config.CommandName} exit 返回。");
                                TShock.Log.Info($"[尘歌壶] {p.Name} 进入 {pot.WorldName}");
                            }
                            catch (Exception ex)
                            {
                                pot.Busy = false;
                                TShock.Log.Error("[尘歌壶] 搬人失败: " + ex);
                                p.SendErrorMessage("[尘歌壶] 进入失败: " + ex.Message);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        pot.Busy = false;
                        TShock.Log.Error("[尘歌壶] 世界准备失败: " + ex);
                        p.SendErrorMessage("[尘歌壶] 准备失败: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                pot.Busy = false;
                TShock.Log.Error("[尘歌壶] 进入流程异常: " + ex);
            }
        });
    }

    private static void Leave(CommandArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }

        if (!Inside.TryGetValue(p.Index, out Pot? pot))
        {
            p.SendInfoMessage("[尘歌壶] 你当前不在壶里。");
            return;
        }

        if (!ReturnTo.TryGetValue(p.Index, out ServerContext? origin))
        {
            Inside.TryRemove(p.Index, out _);
            pot.InsideIndex = -1;
            p.SendInfoMessage("[尘歌壶] 已清理状态（找不到原世界）。");
            return;
        }

        ServerContext? here = UnifiedServerCoordinator.GetClientCurrentlyServer(p.Index);
        if (here is null || ReferenceEquals(here, origin))
        {
            Inside.TryRemove(p.Index, out _);
            pot.InsideIndex = -1;
            pot.LastActiveMs = NowMs;
            p.SendInfoMessage("[尘歌壶] 你其实还在原世界，已为你清理状态。");
            return;
        }

        p.SendInfoMessage("[尘歌壶] 正在返回…");
        here.Dispatcher.Post(() =>
        {
            try
            {
                UnifiedServerCoordinator.TransferPlayerToServer((byte)p.Index, origin);
                Inside.TryRemove(p.Index, out _);
                ReturnTo.TryRemove(p.Index, out _);
                pot.InsideIndex = -1;
                pot.LastActiveMs = NowMs;
                p.SendSuccessMessage("[尘歌壶] 已返回原世界。");
                TShock.Log.Info($"[尘歌壶] {p.Name} 离开 {pot.WorldName}");
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[尘歌壶] 返回失败: " + ex);
                p.SendErrorMessage("[尘歌壶] 返回失败: " + ex.Message);
            }
        });
    }

    private static void OnLogout(PlayerLogoutEventArgs e)
    {
        try
        {
            if (e.Player is null) { return; }
            int idx = e.Player.Index;
            if (Inside.TryRemove(idx, out Pot? pot))
            {
                pot.InsideIndex = -1;
                pot.LastActiveMs = NowMs;
                pot.Busy = false;
                TShock.Log.Info($"[尘歌壶] {e.Player.Name} 登出，{pot.WorldName} 转为空闲（闲置到期后销毁）");
            }
            ReturnTo.TryRemove(idx, out _);
        }
        catch { }
    }

    private static void IdleTick()
    {
        try
        {
            long now = NowMs;
            long limit = _config.IdleDestroySeconds * 1000L;
            foreach (var kv in Pots.ToArray())
            {
                Pot pot = kv.Value;
                if (pot.Busy || pot.Server is null) { continue; }
                if (pot.InsideIndex >= 0) { continue; }
                if (now - pot.LastActiveMs < limit) { continue; }

                pot.Busy = true;
                Pots.TryRemove(kv.Key, out _);
                ServerContext ctx = pot.Server;
                string name = pot.WorldName;
                TShock.Log.Info($"[尘歌壶] {name} 闲置超时，正在存档并卸载…");
                try
                {
                    ctx.Dispatcher.Post(() =>
                    {
                        try { ctx.WorldFile.SaveWorld(resetTime: false, useTemps: false); } catch (Exception ex) { TShock.Log.Warning("[尘歌壶] 存档失败: " + ex.Message); }
                        try { ctx.Close().Wait(TimeSpan.FromSeconds(60)); } catch { }
                        try { UnifiedServerCoordinator.RemoveServer(ctx); } catch { }
                        TShock.Log.Info($"[尘歌壶] {name} 已成功卸载。");
                    });
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[尘歌壶] 卸载投递失败: " + ex);
                }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[尘歌壶] 闲置清理异常: " + ex.Message); }
    }

    private static string OwnerKey(TSPlayer p)
    {
        try
        {
            string uuid = p.UUID ?? "";
            if (!string.IsNullOrEmpty(uuid) && Guid.TryParse(uuid, out _)) { return uuid; }
        }
        catch { }
        return p.Name ?? ("#" + p.Index);
    }

    private static string ShortKey(string key)
    {
        string k = key.Replace("-", "");
        return k.Length > 12 ? k[..12] : k;
    }

    private static string MakeWorldName(TSPlayer p)
    {
        string n = (p.Name ?? "?").Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) { n = n.Replace(c, '_'); }
        return n + "的尘歌壶";
    }

    private static string WorldFileName(string key) => "尘歌壶_" + ShortKey(key);

    private static string BootName(string key) => "PotBoot_" + ShortKey(key);

    private static IWorldDataProvider BuildProvider(string worldName, int tileW, int tileH)
    {
        string? prev = Environment.GetEnvironmentVariable("UTSL_CUSTOM_HEIGHT");
        try
        {
            Environment.SetEnvironmentVariable("UTSL_CUSTOM_HEIGHT", tileH.ToString());
            return IWorldDataProvider.GenerateOrLoadExisting(worldName, tileW, difficulty: 0, worldEvil: 0, seed: "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UTSL_CUSTOM_HEIGHT", prev);
        }
    }
}
