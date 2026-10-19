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
        int removed = _config.Npcs.RemoveAll(id => id <= 0 || id >= Terraria.ID.NPCID.Count);
        if (removed > 0) { _config.Save(_configPath); TShock.Log.Info($"[BanNpc] 已清理 {removed} 个非法 ID"); }

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
        if (_config.Npcs.Contains(npc.type))
        {
            args.ContinueExecution = false;   // 从生成入口直接拦掉，active=false 会被后续逻辑覆盖
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

        if (!TryResolveNpc(args.Parameters[1], out int npcId, out string npcName))
        {
            Reply(args, "找不到 NPC：" + args.Parameters[1]);
            return;
        }
        switch (args.Parameters[0].ToLowerInvariant())
        {
            case "add":
                if (_config.Npcs.Contains(npcId)) { Reply(args, $"{npcName}({npcId}) 已在表中"); return; }
                _config.Npcs.Add(npcId);
                _config.Save(_configPath);
                Reply(args, $"已阻止 {npcName}({npcId})");
                break;
            case "del":
            case "delete":
            case "remove":
                if (!_config.Npcs.Remove(npcId)) { Reply(args, $"{npcName}({npcId}) 不在表中"); return; }
                _config.Save(_configPath);
                Reply(args, $"已解除 {npcName}({npcId})");
                break;
            default:
                Reply(args, "用法：/bm add|del <名称或ID>");
                break;
        }
    }

    /// <summary>按 ID 或名称解析 NPC：优先精确名，其次包含匹配（避免 GetNPCByIdOrName 返回未初始化 NPC）。</summary>
    private static bool TryResolveNpc(string input, out int id, out string name)
    {
        id = 0;
        name = "";
        if (int.TryParse(input, out int parsed) && parsed > 0 && parsed < Terraria.ID.NPCID.Count)
        {
            id = parsed;
            name = Terraria.Lang.GetNPCNameValue(parsed).ToString();
            return true;
        }

        List<(int Id, string Name)> partial = [];
        for (int i = 1; i < Terraria.ID.NPCID.Count; i++)
        {
            string n = Terraria.Lang.GetNPCNameValue(i).ToString();
            if (string.IsNullOrEmpty(n)) { continue; }
            if (string.Equals(n, input, StringComparison.OrdinalIgnoreCase)) { id = i; name = n; return true; }
            if (n.Contains(input, StringComparison.OrdinalIgnoreCase)) { partial.Add((i, n)); }
        }

        if (partial.Count == 1) { id = partial[0].Id; name = partial[0].Name; return true; }
        if (partial.Count > 1)
        {
            name = string.Join("、", partial.Take(8).Select(x => $"{x.Name}({x.Id})"));
            return false;
        }
        return false;
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); } else { TShock.Log.Info(text); }
    }
}
