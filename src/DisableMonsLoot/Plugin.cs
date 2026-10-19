using System.Collections.Immutable;
using Terraria;
using TShockAPI;
using TrProtocol;
using TrProtocol.Models;
using UnifiedServerProcess;
using TrProtocol.NetPackets;
using Terraria.ID;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace DisableMonsLoot;

/// <summary>击杀指定 Boss 后清掉它掉落的战利品。</summary>
[PluginMetadata("DisableMonsLoot", "1.3.5", "羽学", "清理怪物身边掉落物")]
public sealed class DisableMonsLootPlugin : BasePlugin
{
    private static DisableMonsLootConfig _config = new();
    private static string _configPath = "";
    private static readonly Dictionary<string, HashSet<int>> Downed = new();
    private static readonly object Gate = new();

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
        _config = DisableMonsLootConfig.Load(_configPath);

        HookEvents.Terraria.NPC.checkDead += OnNpcKilled;
        Commands.ChatCommands.Add(new Command("disablemonsloot.admin", Command, "kdm", "禁掉落"));
        TShock.Log.Info("[DisableMonsLoot] 已加载");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { HookEvents.Terraria.NPC.checkDead -= OnNpcKilled; }
        return ValueTask.CompletedTask;
    }

    private static void OnNpcKilled(NPC npc, HookEvents.Terraria.NPC.checkDeadEventArgs args)
    {
        if (!_config.Enabled || npc is null || npc.life > 0) { return; }   // checkDead 每次受伤都调
        ServerContext? server = args.root?.ToServer();
        if (server is null) { return; }

        lock (Gate)
        {
            for (int gi = 0; gi < _config.Groups.Count; gi++)
            {
                DisableMonsLootConfig.Group group = _config.Groups[gi];
                if (Array.IndexOf(group.Ids, npc.netID) < 0) { continue; }

                string key = server.Name + "|" + gi;
                if (!group.Enabled)
                {
                    if (!_config.KillAll) { break; }
                    if (!Downed.TryGetValue(key, out HashSet<int>? set)) { set = []; Downed[key] = set; }
                    set.Add(npc.netID);
                    if (set.Count >= group.Ids.Length) { group.Enabled = false; _config.Save(_configPath); }
                    break;
                }

                if (!_config.KillAll)
                {
                    group.Enabled = false;
                    _config.Save(_configPath);
                    TShock.Log.Info($"[DisableMonsLoot] 已击杀 {npc.FullName}，关闭该组清理");
                    break;
                }

                ClearItems(server, npc, group.ItemIds);
                break;
            }
        }
    }

    private static void ClearItems(ServerContext server, NPC npc, int[] itemIds)
    {
        float limit = _config.Radius * _config.Radius * 256f;
        for (int i = 0; i < server.Main.item.Length; i++)
        {
            var item = server.Main.item[i];
            if (!item.active || Array.IndexOf(itemIds, item.type) < 0) { continue; }
            float dx = item.position.X - npc.position.X;
            float dy = item.position.Y - npc.position.Y;
            if (dx * dx + dy * dy > limit) { continue; }
            item.TurnToAir(server);
        }
    }

    private static void Command(CommandArgs args)
    {
        if (args.Parameters.Count == 0) { Help(args); return; }
        string sub = args.Parameters[0].ToLowerInvariant();
        switch (sub)
        {
            case "on":
                _config.Enabled = !_config.Enabled;
                _config.Save(_configPath);
                Reply(args, $"[禁怪物掉落] 已{(_config.Enabled ? "启用" : "禁用")}");
                break;
            case "kill":
                _config.KillAll = !_config.KillAll;
                _config.Save(_configPath);
                Reply(args, $"[禁怪物掉落] 全杀已{(_config.KillAll ? "启用" : "禁用")}");
                break;
            case "reset":
                Downed.Clear();
                foreach (DisableMonsLootConfig.Group g in _config.Groups) { g.Enabled = true; }
                _config.Save(_configPath);
                Reply(args, "[禁怪物掉落] 清理列表已全部恢复开启");
                break;
            case "list":
                foreach (DisableMonsLootConfig.Group g in _config.Groups)
                {
                    string names = string.Join("、", g.Ids.Select(id => TShockAPI.Utils.GetNPCById(id)?.FullName ?? id.ToString()).Distinct());
                    Reply(args, $"  [{g.Enabled switch { true => "清理", false => "已关" }}] {names}");
                }
                break;
            case "f":
            case "radius":
                if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out int r) || r < 1) { Reply(args, "用法：/kdm f <格数>"); break; }
                _config.Radius = r;
                _config.Save(_configPath);
                Reply(args, $"[禁怪物掉落] 清理范围 = {r} 格");
                break;
            default:
                Help(args);
                break;
        }
    }

    private static void Help(CommandArgs args)
    {
        Reply(args, "/kdm — 菜单");
        Reply(args, "/kdm on — 开关插件");
        Reply(args, "/kdm list — 查看清理列表");
        Reply(args, "/kdm kill — 开关全杀（杀满整组才关）");
        Reply(args, "/kdm f <格> — 设置清理范围");
        Reply(args, "/kdm reset — 恢复所有清理开关");
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); } else { TShock.Log.Info(text); }
    }
}
