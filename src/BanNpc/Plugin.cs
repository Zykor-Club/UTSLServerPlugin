using System.Collections.Immutable;
using Terraria;
using TShockAPI;
using UnifierTSL.Plugins;

namespace BanNpc;

[PluginMetadata("BanNpc", "1.0.0", "Patrikk,GK 改良", "禁止指定怪物的出没")]
public sealed class BanNpcPlugin : BasePlugin
{
    private static BanNpcConfig _config = new();
    private static string _configPath = "";

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
        _config = BanNpcConfig.Load(_configPath);

        HookEvents.Terraria.NPC.OnSpawn += OnSpawn;
        Commands.ChatCommands.Add(new Command("bannpc.use", Command, "bm"));
        TShock.Log.Info($"[BanNpc] 已加载（阻止 {_config.Npcs.Count} 种）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { HookEvents.Terraria.NPC.OnSpawn -= OnSpawn; }
        return ValueTask.CompletedTask;
    }

    private static void OnSpawn(NPC npc, HookEvents.Terraria.NPC.OnSpawnEventArgs args)
    {
        if (npc is null || _config.Npcs.Count == 0) { return; }
        if (_config.Npcs.Contains(npc.netID))
        {
            npc.active = false;
        }
    }

    private static void Command(CommandArgs args)
    {
        if (args.Parameters.Count == 1 && args.Parameters[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.Npcs.Count == 0) { Reply(args, "阻止表为空。"); return; }
            string names = string.Join("、", _config.Npcs.Select(id => $"{TShockAPI.Utils.GetNPCById(id)?.FullName ?? "?"}({id})"));
            Reply(args, "阻止表：" + names);
            return;
        }

        if (args.Parameters.Count != 2)
        {
            Reply(args, "/bm list");
            Reply(args, "/bm add <名称或ID>");
            Reply(args, "/bm del <名称或ID>");
            return;
        }

        List<NPC> matched = TShockAPI.Utils.GetNPCByIdOrName(args.Parameters[1]);
        if (matched.Count == 0) { Reply(args, "无效 NPC：" + args.Parameters[1]); return; }
        if (matched.Count > 1) { Reply(args, "匹配到多个，请用 ID：" + string.Join("、", matched.Select(n => $"{n.FullName}({n.netID})"))); return; }

        NPC npc = matched[0];
        switch (args.Parameters[0].ToLowerInvariant())
        {
            case "add":
                if (_config.Npcs.Contains(npc.netID)) { Reply(args, $"{npc.FullName}({npc.netID}) 已在表中"); return; }
                _config.Npcs.Add(npc.netID);
                _config.Save(_configPath);
                Reply(args, $"已阻止 {npc.FullName}({npc.netID})");
                break;
            case "del":
            case "delete":
            case "remove":
                if (!_config.Npcs.Remove(npc.netID)) { Reply(args, $"{npc.FullName}({npc.netID}) 不在表中"); return; }
                _config.Save(_configPath);
                Reply(args, $"已解除 {npc.FullName}({npc.netID})");
                break;
            default:
                Reply(args, "用法：/bm add|del <名称或ID>");
                break;
        }
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); } else { TShock.Log.Info(text); }
    }
}
