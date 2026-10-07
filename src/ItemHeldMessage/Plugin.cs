// UTSL 差异：上游的 Terraria.PacketTypes 被替换成 TrProtocol.MessageID，
// TShockAPI 内部用全局别名保持源码兼容，插件里也要这样声明
global using PacketTypes = TrProtocol.MessageID;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace ItemHeldMessage;

/// <summary>
/// ItemHeldMessage 手持物品提示（UTSL 版）
///
/// 与 TShock 版的差异：
///   - 入口：TerrariaPlugin + [ApiVersion] -> BasePlugin + [PluginMetadata]
///   - 每帧检测：ServerApi.Hooks.GameUpdate -> UnifierApi.EventHub.Game.PostUpdate
///     （多世界：按 ServerContext 遍历，只处理属于该世界的玩家）
///   - 玩家离开：ServerApi.Hooks.ServerLeave -> UnifierApi.EventHub.Netplay.LeaveEvent
///   - 进入提示：ServerApi.Hooks.NetGreetPlayer -> TShockAPI.Hooks.PlayerHooks.PlayerPostLogin
///     （UTSL 没有 NetGreetPlayer；改用登录完成事件，未登录的 guest 不会收到欢迎语）
///   - 浮动文本：NetMessage.SendData -> server.NetMessage.SendData（USP 把 NetMessage 上下文化了）
///   - 执行命令：Commands.HandleCommand(player, cmd) -> Commands.HandleCommand(CommandExecutor, cmd)
///   - 会话表按 (ServerContext, 玩家槽位) 建立，避免多世界槽位冲突
///   - 日志：TShock.Log.Warn -> Warning；TShock.Utils -> Utils
///   - 配置：Newtonsoft.Json -> System.Text.Json，目录改为 config/ItemHeldMessage/
/// </summary>
[PluginMetadata("ItemHeldMessage", "1.1.4", "淦 & 星梦XM", "手持物品显示提示与自动执行命令")]
public class ItemHeldMessagePlugin : BasePlugin
{
    private const string PluginVersion = "1.1.4";

    private static PluginConfig Config = new();

    /// <summary>按 (世界, 玩家槽位) 存会话，多世界下槽位会重复</summary>
    private readonly ConcurrentDictionary<(ServerContext?, int), PlayerSession> _sessions = new();

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

        PluginConfig.Initialize(configRegistrar.Directory);
        if (!LoadConfig())
        {
            TShock.Log.Warning("[手持提示] 配置加载失败，使用默认配置");
        }

        GeneralHooks.ReloadEvent += OnReload;
        UnifierApi.EventHub.Game.PostUpdate.Register(OnGamePostUpdate, HandlerPriority.Normal);
        UnifierApi.EventHub.Netplay.LeaveEvent.Register(OnLeave, HandlerPriority.Normal);
        PlayerHooks.PlayerPostLogin += OnPlayerPostLogin;

        Commands.ChatCommands.Add(new Command("itemheldmsg.use", MainCommand, "ihm", "手持提示")
        {
            HelpText = "手持物品提示插件，使用 /ihm help 查看帮助"
        });

        TShock.Log.Info($"[手持提示] 插件已加载 v{PluginVersion}");
    }

    public override ValueTask DisposeAsync(bool isDisposing)
    {
        if (isDisposing)
        {
            GeneralHooks.ReloadEvent -= OnReload;
            UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnGamePostUpdate);
            UnifierApi.EventHub.Netplay.LeaveEvent.UnRegister(OnLeave);
            PlayerHooks.PlayerPostLogin -= OnPlayerPostLogin;

            Assembly asm = Assembly.GetExecutingAssembly();
            Commands.ChatCommands.RemoveAll(c => c.CommandDelegate.Method?.DeclaringType?.Assembly == asm);

            _sessions.Clear();
        }

        return base.DisposeAsync(isDisposing);
    }

    // 配置加载与保存
    private static bool LoadConfig()
    {
        try
        {
            if (!File.Exists(PluginConfig.ConfigPath))
            {
                Config = PluginConfig.CreateDefault();
                SaveConfig();
                return true;
            }

            string json = File.ReadAllText(PluginConfig.ConfigPath);
            PluginConfig? loaded = PluginConfig.Load(json);
            if (loaded != null)
            {
                Config = loaded;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            TShock.Log.Error($"[手持提示] 配置加载异常: {ex.Message}");
            Config = PluginConfig.CreateDefault();
            return false;
        }
    }

    private static bool SaveConfig()
    {
        try
        {
            File.WriteAllText(PluginConfig.ConfigPath, Config.Serialize());
            return true;
        }
        catch (Exception ex)
        {
            TShock.Log.Error($"[手持提示] 配置保存失败: {ex.Message}");
            return false;
        }
    }

    // /reload 命令回调
    private void OnReload(ReloadEventArgs args)
    {
        if (args.Player != null && !args.Player.HasPermission("itemheldmsg.admin"))
        {
            args.Player.SendErrorMessage("[手持提示] 权限不足！需要 itemheldmsg.admin");
            return;
        }

        if (LoadConfig())
        {
            args.Player?.SendSuccessMessage("[手持提示] 配置已重载");
        }
        else
        {
            args.Player?.SendErrorMessage("[手持提示] 配置重载失败");
        }
    }

    /// <summary>命令执行者：UTSL 控制台执行时 args.Player 为 null，回退到 ExecutorActor</summary>
    private static TSPlayer ArgPlayer(CommandArgs args) => args.Player ?? args.ExecutorActor;

    // 主命令处理
    private void MainCommand(CommandArgs args)
    {
        TSPlayer player = ArgPlayer(args);
        List<string> param = args.Parameters;

        if (param.Count == 0 || param[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            ShowHelp(player);
            return;
        }

        switch (param[0].ToLower())
        {
            case "mode":
                HandleModeCommand(player, param.Count > 1 ? param[1] : null);
                break;
            case "status":
                ShowStatus(player);
                break;
            case "check":
                if (param.Count < 2) player.SendErrorMessage("用法: /ihm check <物品ID或名称>");
                else CheckItem(player, param[1]);
                break;
            case "reload":
                if (!player.HasPermission("itemheldmsg.admin"))
                {
                    player.SendErrorMessage("权限不足！需要 itemheldmsg.admin");
                    return;
                }

                if (LoadConfig())
                    player.SendSuccessMessage("[手持提示] 配置已重载");
                else
                    player.SendErrorMessage("[手持提示] 配置重载失败");
                break;
            default:
                player.SendErrorMessage("未知命令，使用 /ihm help 查看帮助");
                break;
        }
    }

    // 显示帮助信息
    private static void ShowHelp(TSPlayer player)
    {
        StringBuilder sb = new();
        sb.AppendLine("[c/55CDFF:★ 手持物品提示 v1.1.4 ★]");
        sb.AppendLine("[c/FFD700:/ihm] - 显示帮助");
        sb.AppendLine("[c/FFD700:/ihm mode [0-3]] - 设置显示模式 (0关 1浮动 2信息栏 3全)");
        sb.AppendLine("[c/FFD700:/ihm status] - 查看状态");
        sb.AppendLine("[c/FFD700:/ihm check <物品>] - 查看物品配置详情");
        if (player.HasPermission("itemheldmsg.admin"))
        {
            sb.AppendLine("[c/FF6B6B:/ihm reload] - 重载配置");
            sb.AppendLine("[c/FF6B6B:/reload] - 也可重载本插件配置");
        }
        player.SendMessage(sb.ToString(), Color.Cyan);
    }

    // 处理模式切换命令
    private void HandleModeCommand(TSPlayer player, string? modeStr)
    {
        PlayerSession session = GetSession(player);

        if (string.IsNullOrEmpty(modeStr))
        {
            string[] names = { "关闭", "浮动文本", "信息栏", "全部" };
            player.SendSuccessMessage($"当前模式: {names[session.DisplayMode]} ({session.DisplayMode})");
            return;
        }

        if (!int.TryParse(modeStr, out int mode) || mode is < 0 or > 3)
        {
            player.SendErrorMessage("无效模式！请输入 0-3");
            return;
        }

        session.DisplayMode = mode;
        string[] modeNames = { "关闭", "浮动文本", "信息栏", "全部" };
        player.SendSuccessMessage($"显示模式已切换为: {modeNames[mode]}");
    }

    // 显示插件状态
    private void ShowStatus(TSPlayer player)
    {
        PlayerSession session = GetSession(player);
        GlobalSettings g = Config.Global;

        player.SendMessage(
            $"[c/55CDFF:★ 系统状态 ★]\n" +
            $"[c/CCCCCC:浮动文本: {(g.EnableFloatText ? "开" : "关")} | 信息栏: {(g.EnableChatText ? "开" : "关")} | 命令: {(g.EnableCommand ? "开" : "关")}]\n" +
            $"[c/CCCCCC:切换冷却: {g.SwitchCooldown}秒 | 你的模式: {session.DisplayMode}]\n" +
            $"[c/CCCCCC:全局跳过权限: {(g.SkipCommandPermissionCheck ? "[c/FF6B6B:是]" : "[c/00FF00:否]")}]\n" +
            $"[c/CCCCCC:已配置物品: {Config.Items.Count}个]",
            Color.Cyan
        );
    }

    // 查看物品详细配置
    private void CheckItem(TSPlayer player, string input)
    {
        int itemId;

        // 支持ID或名称查询
        if (!int.TryParse(input, out itemId))
        {
            // 注意要写全名：Terraria.Utils 与 TShockAPI.Utils 同名
            List<Item> items = TShockAPI.Utils.GetItemByName(input);
            if (items.Count == 0) { player.SendErrorMessage($"未找到: {input}"); return; }
            if (items.Count > 1) { player.SendMultipleMatchError(items.Select(i => $"{i.Name}({i.type})")); return; }
            itemId = items[0].type;
        }

        string name = Lang.GetItemNameValue(itemId) ?? $"物品{itemId}";
        if (!Config.Items.TryGetValue(itemId.ToString(), out ItemDefinition? def))
        {
            player.SendMessage($"[c/FF6B6B:{name} (ID:{itemId})] 未配置", Color.White);
            return;
        }

        StringBuilder sb = new();
        sb.AppendLine($"[c/55CDFF:★ {name} (ID:{itemId}) ★]");

        if (def.FloatMessages.Count > 0)
        {
            sb.AppendLine($"[c/00FF00:浮动消息 ({def.FloatMessages.Count}条):]");
            def.FloatMessages.ForEach(m => sb.AppendLine($"  [c/FFD700:] {m.Text}"));
        }

        if (def.ChatMessages.Count > 0)
        {
            sb.AppendLine($"[c/00FF00:信息栏消息 ({def.ChatMessages.Count}条):]");
            def.ChatMessages.ForEach(m => sb.AppendLine($"  [c/FFD700:] {m.Text}"));
        }

        if (def.Command.Enabled)
        {
            sb.AppendLine($"[c/00FF00:自动命令:] {def.Command.Cmd}");
            sb.AppendLine($"  [c/CCCCCC:权限组: {string.Join(", ", def.Command.AllowedGroups)}]");
            sb.AppendLine($"  [c/CCCCCC:跳过权限: {(def.Command.SkipPermissionCheck ? "[c/FF6B6B:是]" : "否")}]");
        }

        player.SendMessage(sb.ToString(), Color.Cyan);
    }

    /// <summary>
    /// 每帧检测手持物品变化（对应上游 ServerApi.Hooks.GameUpdate）。
    /// UTSL 多世界：每次只处理触发事件的那个 ServerContext。
    /// </summary>
    private void OnGamePostUpdate(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        if (!Config.Global.EnableFloatText && !Config.Global.EnableChatText && !Config.Global.EnableCommand)
            return;

        ServerContext server = args.Content.Server;

        foreach (TSPlayer? player in TShock.Players)
        {
            if (player is null || !player.Active || !player.IsLoggedIn) continue;

            // 只处理当前这个世界里的玩家
            if (!ReferenceEquals(player.GetCurrentServer(), server)) continue;

            var currentItem = player.TPlayer.inventory[player.TPlayer.selectedItem].type;
            if (currentItem <= 0) continue;

            PlayerSession session = GetSession(player);

            // 物品未变化则跳过
            if (currentItem == session.LastHeldItem) continue;

            // 检查切换冷却
            TimeSpan timeSinceSwitch = DateTime.UtcNow - session.LastSwitchTime;
            if (timeSinceSwitch.TotalSeconds < Config.Global.SwitchCooldown)
            {
                session.LastHeldItem = currentItem;
                continue;
            }

            session.LastSwitchTime = DateTime.UtcNow;
            session.LastHeldItem = currentItem;

            // 检查物品是否有配置
            if (!Config.Items.TryGetValue(currentItem.ToString(), out ItemDefinition? itemConfig)) continue;
            if (session.DisplayMode == 0) continue;

            // 处理各类消息和命令
            if (Config.Global.EnableFloatText && (session.DisplayMode == 1 || session.DisplayMode == 3))
                ProcessFloatText(server, player, itemConfig, session);

            if (Config.Global.EnableChatText && (session.DisplayMode == 2 || session.DisplayMode == 3))
                ProcessChatText(player, itemConfig, session);

            if (Config.Global.EnableCommand && itemConfig.Command.Enabled)
                ProcessCommand(server, player, itemConfig, session);
        }
    }

    // 处理浮动文本（头顶显示）
    private void ProcessFloatText(ServerContext server, TSPlayer player, ItemDefinition config, PlayerSession session)
    {
        double cooldown = config.OverrideSettings?.FloatTextCooldown ?? Config.Global.FloatTextCooldown;

        if (!session.CanExecute("float", cooldown)) return;
        if (config.FloatMessages.Count == 0) return;

        MessageData msg = config.FloatMessages[Random.Shared.Next(config.FloatMessages.Count)];
        float yOffset = config.OverrideSettings?.YOffset ?? Config.Global.DefaultYOffset;
        Vector2 pos = player.TPlayer.Center;
        pos.Y -= yOffset;

        Color color = msg.Color.Length >= 3
            ? new Color(msg.Color[0], msg.Color[1], msg.Color[2])
            : Color.White;

        // 发给该世界所有客户端（USP 下 NetMessage 是 per-context 的）
        server.NetMessage.SendData(
            (int)PacketTypes.CreateCombatTextExtended,
            remoteClient: -1,
            ignoreClient: -1,
            text: NetworkText.FromLiteral(msg.Text),
            number: (int)color.PackedValue,
            number2: pos.X,
            number3: pos.Y
        );

        session.MarkExecuted("float");
    }

    // 处理聊天栏消息（仅自己可见）
    private void ProcessChatText(TSPlayer player, ItemDefinition config, PlayerSession session)
    {
        double cooldown = config.OverrideSettings?.ChatTextCooldown ?? Config.Global.ChatTextCooldown;
        if (!session.CanExecute("chat", cooldown)) return;

        if (config.ChatMessages.Count == 0) return;
        MessageData msg = config.ChatMessages[Random.Shared.Next(config.ChatMessages.Count)];

        string colorHex = msg.Color.Length >= 3
            ? $"{msg.Color[0]:X2}{msg.Color[1]:X2}{msg.Color[2]:X2}"
            : "FFFFFF";

        player.SendMessage($"[c/{colorHex}:{msg.Text}]", Color.White);
        session.MarkExecuted("chat");
    }

    // 处理自动命令执行
    private void ProcessCommand(ServerContext server, TSPlayer player, ItemDefinition config, PlayerSession session)
    {
        CommandSettings cmd = config.Command;

        // 检查是否跳过权限检测（全局开关或单物品开关）
        bool skipPermission = Config.Global.SkipCommandPermissionCheck || cmd.SkipPermissionCheck;

        if (!skipPermission && !CheckPermission(player, cmd.AllowedGroups))
        {
            player.SendErrorMessage("权限不足，无法执行手持命令");
            return;
        }

        double cooldown = config.OverrideSettings?.CommandCooldown ?? Config.Global.CommandCooldown;
        if (!session.CanExecute("cmd", cooldown)) return;

        // 变量替换
        string command = cmd.Cmd
            .Replace("{player}", player.Name)
            .Replace("{item}", session.LastHeldItem.ToString())
            .Replace("{x}", ((int)player.X).ToString())
            .Replace("{y}", ((int)player.Y).ToString());

        try
        {
            // UTSL 的命令执行入口接收 CommandExecutor，不再是 TSPlayer
            CommandExecutor executor = new(server, (byte)player.Index);
            Commands.HandleCommand(executor, command);
            session.MarkExecuted("cmd");
            TShock.Log.Info($"[手持提示] {player.Name} 执行: {command}");
        }
        catch (Exception ex)
        {
            TShock.Log.Error($"[手持提示] 命令执行失败: {ex.Message}");
        }
    }

    // 检查玩家权限组
    private static bool CheckPermission(TSPlayer player, List<string> allowedGroups)
    {
        if (allowedGroups == null || allowedGroups.Count == 0) return true;
        return allowedGroups.Contains(player.Group.Name, StringComparer.OrdinalIgnoreCase);
    }

    // 玩家登录完成（对应上游 NetGreetPlayer）- 发送欢迎消息
    private void OnPlayerPostLogin(PlayerPostLoginEventArgs e)
    {
        e.Player?.SendInfoMessage("[c/55CDFF:手持物品提示] 已加载！使用 /ihm 查看帮助");
    }

    // 玩家离开（对应上游 ServerLeave）- 清理会话
    private void OnLeave(ref ReadonlyNoCancelEventArgs<LeaveEvent> args)
    {
        _sessions.TryRemove((args.Content.Server, args.Content.Who), out _);
    }

    // 获取或创建玩家会话
    private PlayerSession GetSession(TSPlayer player)
        => GetSession((player.GetCurrentServer(), player.Index));

    private PlayerSession GetSession((ServerContext?, int) key)
        => _sessions.GetOrAdd(key, _ => new PlayerSession());
}

// 玩家会话类 - 存储每个玩家的状态
public sealed class PlayerSession
{
    public int LastHeldItem { get; set; } = -1; // 上次手持物品ID
    public int DisplayMode { get; set; } = 3;   // 显示模式（默认全部）
    public DateTime LastSwitchTime { get; set; } = DateTime.MinValue; // 上次切换时间
    private readonly Dictionary<string, DateTime> _cooldowns = new(); // 各类型冷却记录

    // 检查是否已过冷却时间
    public bool CanExecute(string type, double cooldownSeconds)
    {
        if (cooldownSeconds <= 0) return true;
        if (!_cooldowns.TryGetValue(type, out DateTime last)) return true;
        return (DateTime.UtcNow - last).TotalSeconds >= cooldownSeconds;
    }

    // 标记已执行，更新冷却时间
    public void MarkExecuted(string type) => _cooldowns[type] = DateTime.UtcNow;
}
