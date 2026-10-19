using System.Collections.Immutable;
using Terraria;
using TShockAPI;
using TShockAPI.Hooks;
using TrProtocol.NetPackets;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;

namespace AutoTeam;

[PluginMetadata("AutoTeam", "2.4.9", "十七改，肝帝熙恩改", "自动分配一个组的玩家到特定队伍")]
public sealed class AutoTeamPlugin : BasePlugin
{
    private static AutoTeamConfig _config = new();
    private static string _configPath = "";
    private static long _lastHeal;

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
        _config = AutoTeamConfig.Load(_configPath);

        PlayerHooks.PlayerPostLogin += OnLogin;
        NetPacketHandler.Register<PlayerTeam>(OnTeamPacket, HandlerPriority.Normal);
        Commands.ChatCommands.Add(new Command("autoteam.toggle", Toggle, "autoteam", "at"));
        TShock.Log.Info("[AutoTeam] 已加载");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            PlayerHooks.PlayerPostLogin -= OnLogin;
            NetPacketHandler.UnRegister<PlayerTeam>(OnTeamPacket);
        }
        return ValueTask.CompletedTask;
    }

    private static void OnLogin(PlayerPostLoginEventArgs args)
    {
        if (args.Player is null) { return; }
        Assign(args.Player);
    }

    private static void OnTeamPacket(ref ReceivePacketEvent<PlayerTeam> args)
    {
        TSPlayer? player = args.Who >= 0 && args.Who < TShock.Players.Length ? TShock.Players[args.Who] : null;
        if (player is null || !Assign(player)) { return; }
        args.HandleMode = PacketHandleMode.Cancel;
    }

    private static bool Assign(TSPlayer player)
    {
        if (!_config.Enabled || player.Group is null) { return false; }
        if (player.Group.HasPermission("noautoteam")) { return false; }
        if (!_config.GroupTeam.TryGetValue(player.Group.Name, out string? teamName)) { return false; }

        int index = TeamIndex(teamName);
        if (index < 0) { player.SendInfoMessage($"队伍配置错误，您当前队伍为 {teamName}"); return false; }
        player.SetTeam(index);
        player.SendInfoMessage($"你的队伍已切换为 {teamName}.");
        return true;
    }

    private static int TeamIndex(string name) => name.ToLowerInvariant() switch
    {
        "none" or "无队伍" => 0,
        "red" or "红队" => 1,
        "green" or "绿队" => 2,
        "blue" or "蓝队" => 3,
        "yellow" or "黄队" => 4,
        "pink" or "粉队" => 5,
        _ => -1,
    };

    private static void Toggle(CommandArgs args)
    {
        _config.Enabled = !_config.Enabled;
        _config.Save(_configPath);
        Reply(args, $"[AutoTeam] 已{(_config.Enabled ? "启用" : "禁用")}");
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); } else { TShock.Log.Info(text); }
    }
}
