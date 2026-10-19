using System.Collections.Immutable;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;

namespace AutoBroadcast;

[PluginMetadata("AutoBroadcast", "1.1.4", "Scavenger,Cai", "自动广播插件")]
public sealed class AutoBroadcastPlugin : BasePlugin
{
    private static AutoBroadcastConfig _config = new();
    private static string _configPath = "";
    private static Timer? _timer;

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

        _configPath = Path.Combine(configRegistrar.Directory, "settings.json");
        _config = AutoBroadcastConfig.Load(_configPath);
        ResetTimers();

        UnifierApi.EventHub.Chat.ChatEvent.Register(OnChat, HandlerPriority.Normal);
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
        Commands.ChatCommands.Add(new Command("autobroadcast.admin", Command, "ab"));
        TShock.Log.Info($"[AutoBroadcast] 已加载（{_config.Broadcasts.Count} 条广播）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            try { _timer?.Dispose(); } catch { }
            UnifierApi.EventHub.Chat.ChatEvent.UnRegister(OnChat);
        }
        return ValueTask.CompletedTask;
    }

    private static void ResetTimers()
    {
        foreach (BroadcastEntry b in _config.Broadcasts)
        {
            b.Timer = Math.Max(1, b.Interval);
            b.Started = false;
        }
    }

    private static void Tick()
    {
        try
        {
            foreach (BroadcastEntry b in _config.Broadcasts)
            {
                if (!b.Enabled || b.Interval <= 0) { continue; }

                if (b.DelayLeft > 0)
                {
                    b.DelayLeft--;
                    if (b.DelayLeft == 0) { Run(b); }
                    continue;
                }

                b.Timer--;
                if (b.Timer > 0) { continue; }
                b.Timer = b.Interval;
                if (b.StartDelay > 0) { b.DelayLeft = b.StartDelay; }
                else { Run(b); }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[AutoBroadcast] 定时异常: " + ex.Message); }
    }

    private static void Run(BroadcastEntry b)
    {
        if (b.Groups.Length > 0)
        {
            foreach (TSPlayer p in TShock.Players)
            {
                if (p is null || !p.Active || p.Group is null) { continue; }
                if (!b.Groups.Contains(p.Group.Name, StringComparer.OrdinalIgnoreCase)) { continue; }
                Send(p, b);
            }
        }
        else
        {
            foreach (TSPlayer p in TShock.Players)
            {
                if (p is not null && p.Active) { Send(p, b); }
            }
        }
    }

    private static void Send(TSPlayer p, BroadcastEntry b)
    {
        int r = b.ColorRgb.Length > 0 ? Math.Clamp(b.ColorRgb[0], 0, 255) : 255;
        int g = b.ColorRgb.Length > 1 ? Math.Clamp(b.ColorRgb[1], 0, 255) : 255;
        int bl = b.ColorRgb.Length > 2 ? Math.Clamp(b.ColorRgb[2], 0, 255) : 255;

        foreach (string line in b.Messages)
        {
            if (line.StartsWith(TShock.Config.GlobalSettings.CommandSpecifier) || line.StartsWith(TShock.Config.GlobalSettings.CommandSilentSpecifier))
            {
                try
                {
                    UnifierTSL.Servers.ServerContext? srv = UnifierTSL.UnifiedServerCoordinator.Servers.FirstOrDefault(x => x.IsRunning);
                    if (srv is not null) { TShockConsoleCommand.TryExecute(line, out _, srv); }
                }
                catch { }
            }
            else
            {
                try { p.SendMessage(line, (byte)r, (byte)g, (byte)bl); } catch { }
            }
        }
    }

    private static void OnChat(ref ValueEventArgs<ChatEvent> args)
    {
        ChatEvent e = args.Content;
        TSPlayer? p = e.Who >= 0 && e.Who < TShock.Players.Length ? TShock.Players[e.Who] : null;
        if (p is null) { return; }

        foreach (BroadcastEntry b in _config.Broadcasts)
        {
            if (!b.Enabled || b.TriggerWords.Length == 0) { continue; }
            if (!b.TriggerWords.Any(w => !string.IsNullOrEmpty(w) && e.Text.Contains(w))) { continue; }

            // 上游语义：配了组时，玩家在该组里则直接 return（连后续广播也不再检查）
            if (b.Groups.Length > 0 && b.Groups.Contains(p.Group?.Name ?? "", StringComparer.OrdinalIgnoreCase)) { return; }

            if (b.TriggerToWholeGroup) { BroadcastGroups(b); }
            else { Send(p, b); }
            break;
        }
    }

    private static void BroadcastGroups(BroadcastEntry b)
    {
        foreach (TSPlayer p in TShock.Players)
        {
            if (p is null || !p.Active || p.Group is null) { continue; }
            if (!b.Groups.Contains(p.Group.Name, StringComparer.OrdinalIgnoreCase)) { continue; }
            Send(p, b);
        }
    }

    private static void Command(CommandArgs args)
    {
        if (args.Parameters.Count == 0 || args.Parameters[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (BroadcastEntry b in _config.Broadcasts)
            {
                Reply(args, $"{(b.Enabled ? "[开]" : "[关]")} {b.Name} 间隔={b.Interval}s 触发词={b.TriggerWords.Length}");
            }
            return;
        }

        if (args.Parameters[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            _config = AutoBroadcastConfig.Load(_configPath);
            ResetTimers();
            Reply(args, "已重载配置");
        }
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); } else { TShock.Log.Info(text); }
    }
}
