using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Terraria;
using Terraria.GameContent;
using TShockAPI;
using UnifierTSL.Servers;

namespace BossCommandExecutor
{
    public class BossTracker : IDisposable
    {
        // UTSL 是多世界单进程：NPC 槽位在每个世界都会重复，
        // 因此所有索引都必须带上 ServerContext，否则一个世界的 Boss 会被另一个世界顶掉。
        private readonly ConcurrentDictionary<(ServerContext Server, int WhoAmI), int> _aliveInstances = new();

        private readonly ConcurrentDictionary<(ServerContext Server, int NetId), byte> _spawnedTypes = new();

        private readonly ConcurrentDictionary<string, ConcurrentHashSet<(ServerContext Server, int WhoAmI)>> _compositeBossSegments = new();

        private readonly Timer _cleanupTimer;

        public static readonly Dictionary<int, int[]> MultiSegmentBossMap = new()
        {
            { 13, new[] { 13, 14, 15 } },
            { 14, new[] { 13, 14, 15 } },
            { 15, new[] { 13, 14, 15 } },
            { 266, new[] { 266, 267 } },
            { 267, new[] { 266, 267 } },
            { 35, new[] { 35, 36 } },
            { 36, new[] { 35, 36 } },
            { 113, new[] { 113, 114 } },
            { 114, new[] { 113, 114 } },
            { 125, new[] { 125, 126 } },
            { 126, new[] { 125, 126 } },
            { 127, new[] { 127, 128, 129, 130, 131 } },
            { 128, new[] { 127, 128, 129, 130, 131 } },
            { 129, new[] { 127, 128, 129, 130, 131 } },
            { 130, new[] { 127, 128, 129, 130, 131 } },
            { 131, new[] { 127, 128, 129, 130, 131 } },
            { 134, new[] { 134, 135, 136 } },
            { 135, new[] { 134, 135, 136 } },
            { 136, new[] { 134, 135, 136 } },
            { 245, new[] { 245, 246, 247, 248 } },
            { 246, new[] { 245, 246, 247, 248 } },
            { 247, new[] { 245, 246, 247, 248 } },
            { 248, new[] { 245, 246, 247, 248 } },
            { 396, new[] { 396, 397, 398 } },
            { 397, new[] { 396, 397, 398 } },
            { 398, new[] { 396, 397, 398 } }
        };

        public BossTracker()
        {
            _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>体节分组键必须带上世界，否则不同世界的同名 Boss 会被合并成一组</summary>
        private static string SegmentKey(ServerContext server, IEnumerable<int> types)
            => server.Name + "#" + string.Join(",", types.OrderBy(t => t));

        public void MarkAsAlive(ServerContext server, int whoAmI, int netID, string bossName)
        {
            _aliveInstances[(server, whoAmI)] = netID;
            _spawnedTypes[(server, netID)] = 1;

            if (MultiSegmentBossMap.TryGetValue(netID, out var segmentTypes))
            {
                var key = SegmentKey(server, segmentTypes);
                var segments = _compositeBossSegments.GetOrAdd(key, _ => new ConcurrentHashSet<(ServerContext, int)>());
                segments.Add((server, whoAmI));
                TShock.Log.Debug($"[BossCommand] 追踪复合Boss体节: {bossName} (NetID:{netID}, Idx:{whoAmI}, Key:{key})");
            }
            else
            {
                var customDef = NPCDamageTracker.CustomBossDefinitions[netID];
                if (customDef != null && customDef.NPCTypes != null)
                {
                    var key = SegmentKey(server, customDef.NPCTypes);
                    var segments = _compositeBossSegments.GetOrAdd(key, _ => new ConcurrentHashSet<(ServerContext, int)>());
                    segments.Add((server, whoAmI));
                    TShock.Log.Debug($"[BossCommand] 追踪复合Boss体节(NPCDamageTracker): {bossName} (NetID:{netID}, Idx:{whoAmI}, Key:{key})");
                }
                else
                {
                    TShock.Log.Debug($"[BossCommand] 追踪Boss: {bossName} (NetID:{netID}, Idx:{whoAmI})");
                }
            }
        }

        public bool TryProcessDeath(ServerContext server, int whoAmI, int netID)
        {
            bool wasAlive = _aliveInstances.TryRemove((server, whoAmI), out var storedNetId);
            if (!wasAlive) return false;
            if (storedNetId != netID) return false;

            int[]? segmentTypes = null;
            if (MultiSegmentBossMap.TryGetValue(netID, out segmentTypes))
            {
                var key = SegmentKey(server, segmentTypes);
                if (_compositeBossSegments.TryGetValue(key, out var segments))
                {
                    segments.TryRemove((server, whoAmI));

                    foreach (var segmentType in segmentTypes)
                    {
                        if (IsAnyNPCAliveOfType(server, segmentType))
                        {
                            TShock.Log.Debug($"[BossCommand] 复合Boss {key} 还有存活体节(类型:{segmentType})，跳过");
                            return false;
                        }
                    }

                    _compositeBossSegments.TryRemove(key, out _);
                    TShock.Log.Debug($"[BossCommand] 复合Boss {key} 所有体节已死亡，触发命令");
                }
            }
            else
            {
                var customDef = NPCDamageTracker.CustomBossDefinitions[netID];
                if (customDef != null && customDef.NPCTypes != null)
                {
                    segmentTypes = customDef.NPCTypes.ToArray();
                    var key = SegmentKey(server, segmentTypes);
                    if (_compositeBossSegments.TryGetValue(key, out var segments))
                    {
                        segments.TryRemove((server, whoAmI));

                        foreach (var segmentType in segmentTypes)
                        {
                            if (IsAnyNPCAliveOfType(server, segmentType))
                            {
                                TShock.Log.Debug($"[BossCommand] 复合Boss(NPCDamageTracker) {key} 还有存活体节(类型:{segmentType})，跳过");
                                return false;
                            }
                        }

                        _compositeBossSegments.TryRemove(key, out _);
                        TShock.Log.Debug($"[BossCommand] 复合Boss(NPCDamageTracker) {key} 所有体节已死亡，触发命令");
                    }
                }
            }

            return true;
        }

        public bool WasEverSpawned(ServerContext server, int netID) => _spawnedTypes.ContainsKey((server, netID));

        public bool WasAnySpawned(ServerContext server, int[] types)
        {
            foreach (var type in types)
            {
                if (_spawnedTypes.ContainsKey((server, type)))
                    return true;
            }
            return false;
        }

        /// <summary>必须查指定世界自己的 NPC 数组（上游查的是全局 Main.npc）</summary>
        private static bool IsAnyNPCAliveOfType(ServerContext server, int type)
        {
            NPC[] npcs = server.Main.npc;
            for (int i = 0; i < npcs.Length; i++)
            {
                var npc = npcs[i];
                if (npc.active && npc.type == type)
                    return true;
            }
            return false;
        }

        private void Cleanup(object? state)
        {
            var toRemove = new List<(ServerContext, int)>();

            foreach (var key in _aliveInstances.Keys)
            {
                NPC[]? npcs = key.Server.Main?.npc;
                if (npcs == null || key.WhoAmI < 0 || key.WhoAmI >= npcs.Length || !npcs[key.WhoAmI].active)
                    toRemove.Add(key);
            }

            foreach (var key in toRemove)
                _aliveInstances.TryRemove(key, out _);

            foreach (var kvp in _compositeBossSegments)
            {
                kvp.Value.RemoveWhere(k =>
                {
                    NPC[]? npcs = k.Item1.Main?.npc;
                    return npcs == null || k.Item2 < 0 || k.Item2 >= npcs.Length || !npcs[k.Item2].active;
                });

                if (kvp.Value.IsEmpty)
                    _compositeBossSegments.TryRemove(kvp.Key, out _);
            }

            if (toRemove.Count > 0)
                TShock.Log.Debug($"[BossCommand] 清理 {toRemove.Count} 个残留Boss记录");
        }

        public void Dispose() => _cleanupTimer?.Dispose();

        private class ConcurrentHashSet<T>
        {
            private readonly ConcurrentDictionary<T, byte> _dict = new();

            public void Add(T item) => _dict.TryAdd(item, 0);
            public bool TryRemove(T item) => _dict.TryRemove(item, out _);
            public bool IsEmpty => _dict.IsEmpty;

            public void RemoveWhere(Func<T, bool> predicate)
            {
                foreach (var key in _dict.Keys.ToArray())
                {
                    if (predicate(key))
                        _dict.TryRemove(key, out _);
                }
            }
        }
    }
}
