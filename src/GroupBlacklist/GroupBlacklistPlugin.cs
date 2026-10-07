using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria.Localization;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace GroupBlacklistPlugin;

/// <summary>
/// GroupBlacklist 组黑名单（UTSL 版）
///
/// 与 TShock 版的差异：
///   - 入口：TerrariaPlugin + [ApiVersion] -> BasePlugin + [PluginMetadata]
///   - 生命周期：Initialize()/Dispose(bool) -> InitializeAsync()/DisposeAsync(bool)
///   - 定时扫描：ServerApi.Hooks.GameUpdate -> UnifierApi.EventHub.Game.PostUpdate（带 ServerContext）
///   - 日志：TShock.Log.Warn -> TShock.Log.Warning
///   - 配置目录：tshock/ -> config/GroupBlacklist/
///   - 命令执行者：控制台执行时 args.Player 为 null，回退到 args.ExecutorActor
///   - 多世界：扫描只针对触发事件的那个 ServerContext，节流时间戳按世界分别记录
/// </summary>
[PluginMetadata("GroupBlacklist", "1.1.0", "星梦", "禁止指定用户组进入服务器")]
public class GroupBlacklistPlugin : BasePlugin
{
    private const string PluginVersion = "1.1.0";

    private static BlacklistConfig _config = null!;

    /// <summary>
    /// 每个世界单独节流。多世界下共用一个时间戳会导致只有一个世界被扫描。
    /// </summary>
    private readonly Dictionary<ServerContext, DateTime> _lastChecks = new();
    private readonly object _checkLock = new();

    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        // 等 TShock 初始化完成（TShock.Players / TShock.Order 等在这里才可用）
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock")
            {
                await initInfo.InitializationTask;
            }
        }

        BlacklistConfig.Initialize(configRegistrar.Directory);
        _config = BlacklistConfig.Load();

        // 三层拦截，从早到晚：
        //   1) ReceiveFullClientInfoEvent —— 客户端刚上报名字/UUID、尚未选服、更没下发世界数据。
        //      在这里踢掉，客户端连地图都不会加载，而且能带上自定义提示语。
        //   2) PlayerPostLogin            —— 登录后兜底（运行时用户组此时才确定）
        //   3) Game.PostUpdate 定时扫描    —— 处理在线期间被改组的玩家
        UnifierApi.EventHub.Netplay.ReceiveFullClientInfoEvent.Register(OnReceiveFullClientInfo, HandlerPriority.Normal);
        PlayerHooks.PlayerPostLogin += OnPlayerPostLogin;
        GeneralHooks.ReloadEvent += OnReload;
        UnifierApi.EventHub.Game.PostUpdate.Register(OnGamePostUpdate, HandlerPriority.Normal);

        Commands.ChatCommands.Add(new Command("groupblacklist.admin", GbCommand, "gb")
        {
            HelpText = "组黑名单管理，使用 /gb help 查看帮助"
        });

        TShock.Log.Info($"[组黑名单] 插件已加载 v{PluginVersion}");
    }

    public override ValueTask DisposeAsync(bool isDisposing)
    {
        if (isDisposing)
        {
            UnifierApi.EventHub.Netplay.ReceiveFullClientInfoEvent.UnRegister(OnReceiveFullClientInfo);
            PlayerHooks.PlayerPostLogin -= OnPlayerPostLogin;
            GeneralHooks.ReloadEvent -= OnReload;
            UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnGamePostUpdate);

            Assembly asm = Assembly.GetExecutingAssembly();
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate.Method?.DeclaringType?.Assembly == asm);

            lock (_checkLock)
            {
                _lastChecks.Clear();
            }
        }

        return base.DisposeAsync(isDisposing);
    }

    /// <summary>命令执行者：UTSL 控制台执行时 args.Player 为 null，回退到 ExecutorActor</summary>
    private static TSPlayer ArgPlayer(CommandArgs args) => args.Player ?? args.ExecutorActor;

    private void GbCommand(CommandArgs args)
    {
        List<string> p = args.Parameters;
        TSPlayer player = ArgPlayer(args);

        if (p.Count == 0 || p[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowHelp(player);
            return;
        }

        switch (p[0].ToLower())
        {
            case "on":
                _config.Settings.Enabled = true;
                _config.Save();
                player.SendSuccessMessage("[组黑名单] 已开启");
                if (_config.Settings.LogActions)
                    TShock.Log.Info($"[组黑名单] {player.Name} 开启了插件");
                break;

            case "off":
                _config.Settings.Enabled = false;
                _config.Save();
                player.SendSuccessMessage("[组黑名单] 已关闭");
                if (_config.Settings.LogActions)
                    TShock.Log.Info($"[组黑名单] {player.Name} 关闭了插件");
                break;

            case "add":
                if (p.Count < 2) { player.SendErrorMessage("用法: /gb add <组名>"); return; }
                AddGroup(player, p[1]);
                break;

            case "del":
                if (p.Count < 2) { player.SendErrorMessage("用法: /gb del <组名>"); return; }
                RemoveGroup(player, p[1]);
                break;

            case "padd":
                if (p.Count < 2) { player.SendErrorMessage("用法: /gb padd <玩家名>"); return; }
                AddExempt(player, p[1]);
                break;

            case "pdel":
                if (p.Count < 2) { player.SendErrorMessage("用法: /gb pdel <玩家名>"); return; }
                RemoveExempt(player, p[1]);
                break;

            case "list":
                ShowList(player);
                break;

            default:
                player.SendErrorMessage("未知子命令，使用 /gb help 查看帮助");
                break;
        }
    }

    private static void ShowHelp(TSPlayer player)
    {
        player.SendMessage(
            "[c/55CDFF:=== 组黑名单 ===]\n" +
            "[c/FFD700:/gb on/off] - 开启/关闭插件\n" +
            "[c/FFD700:/gb add <组名>] - 添加黑名单组\n" +
            "[c/FFD700:/gb del <组名>] - 移除黑名单组\n" +
            "[c/FFD700:/gb padd <玩家>] - 添加豁免玩家\n" +
            "[c/FFD700:/gb pdel <玩家>] - 移除豁免玩家\n" +
            "[c/FFD700:/gb list] - 查看当前列表\n" +
            "[c/FFD700:/reload] - 重载配置",
            Color.Cyan
        );
    }

    private void AddGroup(TSPlayer player, string groupName)
    {
        if (_config.BlacklistedGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase))
        {
            player.SendErrorMessage($"组 {groupName} 已在黑名单中");
            return;
        }
        _config.BlacklistedGroups.Add(groupName);
        _config.Save();
        player.SendSuccessMessage($"已将组 {groupName} 加入黑名单");
        if (_config.Settings.LogActions)
            TShock.Log.Info($"[组黑名单] {player.Name} 添加黑名单组: {groupName}");
    }

    private void RemoveGroup(TSPlayer player, string groupName)
    {
        int removed = _config.BlacklistedGroups.RemoveAll(
            g => g.Equals(groupName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            player.SendErrorMessage($"组 {groupName} 不在黑名单中");
            return;
        }
        _config.Save();
        player.SendSuccessMessage($"已将组 {groupName} 从黑名单移除");
        if (_config.Settings.LogActions)
            TShock.Log.Info($"[组黑名单] {player.Name} 移除黑名单组: {groupName}");
    }

    private void AddExempt(TSPlayer player, string playerName)
    {
        if (_config.ExemptPlayers.Contains(playerName, StringComparer.OrdinalIgnoreCase))
        {
            player.SendErrorMessage($"玩家 {playerName} 已在豁免名单中");
            return;
        }
        _config.ExemptPlayers.Add(playerName);
        _config.Save();
        player.SendSuccessMessage($"已将玩家 {playerName} 加入豁免名单");
        if (_config.Settings.LogActions)
            TShock.Log.Info($"[组黑名单] {player.Name} 添加豁免玩家: {playerName}");
    }

    private void RemoveExempt(TSPlayer player, string playerName)
    {
        int removed = _config.ExemptPlayers.RemoveAll(
            p => p.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            player.SendErrorMessage($"玩家 {playerName} 不在豁免名单中");
            return;
        }
        _config.Save();
        player.SendSuccessMessage($"已将玩家 {playerName} 从豁免名单移除");
        if (_config.Settings.LogActions)
            TShock.Log.Info($"[组黑名单] {player.Name} 移除豁免玩家: {playerName}");
    }

    private void ShowList(TSPlayer player)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine("[c/55CDFF:=== 组黑名单状态 ===]");
        sb.AppendLine($"[c/CCCCCC:状态: {(_config.Settings.Enabled ? "[c/00FF00:开启]" : "[c/FF6B6B:关闭]")}]");
        sb.AppendLine($"[c/CCCCCC:黑名单组 ({_config.BlacklistedGroups.Count}):] {string.Join(", ", _config.BlacklistedGroups)}");
        sb.AppendLine($"[c/CCCCCC:豁免玩家 ({_config.ExemptPlayers.Count}):] {string.Join(", ", _config.ExemptPlayers)}");
        player.SendMessage(sb.ToString(), Color.Cyan);
    }

    /// <summary>
    /// 最早的一层：客户端刚上报名字/UUID，还没选服务器，更没有下发世界数据。
    /// 在这里直接断线，客户端不会加载地图，而且能显示配置里的"拒绝加入提示信息"。
    /// 注意此时玩家的"运行时用户组"还没建立，只能用注册账号所属组（未注册玩家按 guest 处理）。
    /// </summary>
    private void OnReceiveFullClientInfo(ref ReadonlyEventArgs<ReceiveFullClientInfo> args)
    {
        if (!_config.Settings.Enabled) return;

        string name = args.Content.Player?.name ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return;

        if (IsExempt(name)) return;

        string group = TShock.UserAccounts.GetUserAccountByName(name)?.Group ?? "guest";

        if (IsBlacklisted(group))
        {
            // 这个 Kick 会设置 PendingTermination/PendingTerminationApproved，
            // 协调器随后不会再补一条通用提示
            args.Content.Sender.Kick(NetworkText.FromLiteral(_config.Settings.KickMessage));
            args.Handled = true;
            if (_config.Settings.LogActions)
                TShock.Log.Warning($"[组黑名单] 在加载世界前拦截玩家 {name} (账号组: {group})");
        }
    }

    private void OnPlayerPostLogin(PlayerPostLoginEventArgs e)
    {
        if (!_config.Settings.Enabled) return;

        TSPlayer? player = e.Player;
        if (player == null || !player.IsLoggedIn) return;

        if (IsExempt(player.Name))
        {
            if (_config.Settings.LogActions)
                TShock.Log.Info($"[组黑名单] 豁免玩家 {player.Name} 跳过检查");
            return;
        }

        if (IsBlacklisted(player.Group.Name))
        {
            player.Disconnect(_config.Settings.KickMessage);
            if (_config.Settings.LogActions)
                TShock.Log.Warning($"[组黑名单] 阻止黑名单组玩家 {player.Name} (组: {player.Group.Name}) 进入");
        }
    }

    /// <summary>
    /// 定时扫描在线玩家（对应上游的 ServerApi.Hooks.GameUpdate）。
    /// UTSL 多世界：每次只处理触发事件的那个 ServerContext。
    /// </summary>
    private void OnGamePostUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        if (!_config.Settings.Enabled || !_config.Settings.KickOnlineBlacklist) return;

        ServerContext server = args.Content.Server;

        lock (_checkLock)
        {
            if (_lastChecks.TryGetValue(server, out DateTime last)
                && (DateTime.Now - last).TotalSeconds < _config.Settings.CheckInterval)
            {
                return;
            }

            _lastChecks[server] = DateTime.Now;
        }

        foreach (TSPlayer? player in TShock.Players)
        {
            if (player is null || !player.Active || !player.IsLoggedIn) continue;

            // 只处理当前这个世界里的玩家
            if (!ReferenceEquals(player.GetCurrentServer(), server)) continue;

            if (IsExempt(player.Name)) continue;

            if (IsBlacklisted(player.Group.Name))
            {
                player.Kick(_config.Settings.InGameKickMessage, true, true);
                if (_config.Settings.LogActions)
                    TShock.Log.Warning($"[组黑名单] 踢出在线黑名单玩家 {player.Name} (组: {player.Group.Name})");
            }
        }
    }

    private void OnReload(ReloadEventArgs args)
    {
        _config = BlacklistConfig.Load();
        args.Player?.SendSuccessMessage("[组黑名单] 配置已重载");
        TShock.Log.Info("[组黑名单] 配置已重载");
    }

    private static bool IsBlacklisted(string groupName) =>
        _config.BlacklistedGroups.Contains(groupName, StringComparer.OrdinalIgnoreCase);

    private static bool IsExempt(string playerName) =>
        _config.ExemptPlayers.Contains(playerName, StringComparer.OrdinalIgnoreCase);
}
