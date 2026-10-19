using System.Collections.Immutable;
using Microsoft.Xna.Framework;
using Terraria;
using TShockAPI;
using TrProtocol.Models;
using UnifiedServerProcess;
using TrProtocol.NetPackets;
using Terraria.ID;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace DeathDrop;

/// <summary>怪物死亡随机 / 自定义掉落。</summary>
[PluginMetadata("DeathDrop", "1.0.6", "大豆子，肝帝熙恩更新优化", "怪物死亡随机和自定义掉落物品")]
public sealed class DeathDropPlugin : BasePlugin
{
    private static readonly Random Rng = new();
    private static DeathDropConfig _config = new();

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

        _config = DeathDropConfig.Load(Path.Combine(configRegistrar.Directory, "settings.json"));
        HookEvents.Terraria.NPC.checkDead += OnNpcKilled;
        TShock.Log.Info("[DeathDrop] 已加载");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { HookEvents.Terraria.NPC.checkDead -= OnNpcKilled; }
        return ValueTask.CompletedTask;
    }

    private static void OnNpcKilled(NPC npc, HookEvents.Terraria.NPC.checkDeadEventArgs args)
    {
        if (npc is null || npc.life > 0 || npc.netID <= 0) { return; }
        ServerContext? server = args.root?.ToServer();
        if (server is null) { return; }

        TSPlayer? killer = npc.lastInteraction != 255 && npc.lastInteraction >= 0 && npc.lastInteraction < TShock.Players.Length
            ? TShock.Players[npc.lastInteraction]
            : null;

        if (_config.RandomDrops)
        {
            int id = PickGlobal();
            if (id > 0 && Roll(_config.RandomChance))
            {
                Spawn(server, npc, id, Rng.Next(_config.Min, _config.Max + 1), killer);
            }
        }

        if (_config.CustomDrops)
        {
            foreach (DeathDropConfig.Monster m in _config.Custom)
            {
                if (m.NpcId != npc.netID || !Roll(m.Chance)) { continue; }
                int id = PickMonster(m);
                if (id > 0) { Spawn(server, npc, id, Rng.Next(m.Min, m.Max + 1), killer); }
            }
        }
    }

    private static void Spawn(ServerContext server, NPC npc, int itemId, int stack, TSPlayer? killer)
    {
        TSPlayer? target = killer;
        if (target is null)
        {
            float best = float.MaxValue;
            for (int i = 0; i < TShock.Players.Length; i++)
            {
                TSPlayer? p = TShock.Players[i];
                if (p is null || !p.Active || !ReferenceEquals(p.GetCurrentServer(), server)) { continue; }
                float dx = p.X - npc.position.X;
                float dy = p.Y - npc.position.Y;
                float d = dx * dx + dy * dy;
                if (d < best) { best = d; target = p; }
            }
        }

        target?.GiveItem(itemId, stack);
    }

    private static int PickGlobal()
    {
        if (_config.FullRandom)
        {
            for (int i = 0; i < 32; i++)
            {
                int id = Rng.Next(1, 5454);
                if (!_config.Excluded.Contains(id)) { return id; }
            }
            return 0;
        }
        return _config.Pool.Count == 0 ? 0 : _config.Pool[Rng.Next(_config.Pool.Count)];
    }

    private static int PickMonster(DeathDropConfig.Monster m)
    {
        if (m.FullRandom)
        {
            for (int i = 0; i < 32; i++)
            {
                int id = Rng.Next(1, 5454);
                if (!m.Excluded.Contains(id)) { return id; }
            }
            return 0;
        }
        return m.Pool.Count == 0 ? 0 : m.Pool[Rng.Next(m.Pool.Count)];
    }

    private static bool Roll(int chance) => chance >= 100 || Rng.Next(100) < chance;
}
