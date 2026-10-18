using Terraria;
using TShockAPI;
using UnifierTSL.Servers;

namespace WorldChainSync.Core;

/// <summary>
/// 跨世界状态同步。
///
/// UTSL 的每个世界都有自己独立的 Main / NPC 上下文（源码里没有任何同步逻辑），
/// 所以下面这些世界级状态必须由我们自己搬；每一组都可以在配置里单独关掉。
/// 玩家侧的 buff/血量/背包【不需要】同步：UTSL 换乘迁移的是同一个 Player 对象。
/// </summary>
public static class WorldStateSync

{
    /// <summary>把 from 世界的世界级状态按配置分组拷到 to 世界。</summary>
    public static void CopyWorldState(ServerContext from, ServerContext to, SyncContentConfig c, bool log = false)
    {
        // ---- 时间 ----
        if (c.Time)
        {
            to.Main.time = from.Main.time;
            to.Main.dayTime = from.Main.dayTime;
            to.Main.moonPhase = from.Main.moonPhase;
            to.Main.moonType = from.Main.moonType;
            to.Main.numStars = from.Main.numStars;
            to.Main.fastForwardTimeToDawn = from.Main.fastForwardTimeToDawn;
            to.Main.fastForwardTimeToDusk = from.Main.fastForwardTimeToDusk;
            to.Main.sundialCooldown = from.Main.sundialCooldown;
            to.Main.moondialCooldown = from.Main.moondialCooldown;
        }

        // ---- 稀有事件（各自独立开关）----
        if (c.BloodMoon) { to.Main.bloodMoon = from.Main.bloodMoon; }
        if (c.Eclipse) { to.Main.eclipse = from.Main.eclipse; }
        if (c.PumpkinMoon) { to.Main.pumpkinMoon = from.Main.pumpkinMoon; }
        if (c.SnowMoon) { to.Main.snowMoon = from.Main.snowMoon; }
        if (c.SlimeRain)
        {
            to.Main.slimeRain = from.Main.slimeRain;
            to.Main.slimeRainTime = from.Main.slimeRainTime;
        }

        // ---- 天气 ----
        if (c.Weather)
        {
            to.Main.raining = from.Main.raining;
            to.Main.rainTime = from.Main.rainTime;
            to.Main.maxRaining = from.Main.maxRaining;
            to.Main.windSpeedCurrent = from.Main.windSpeedCurrent;
            to.Main.windSpeedTarget = from.Main.windSpeedTarget;
            to.Main.numClouds = from.Main.numClouds;
            to.Main.cloudBGActive = from.Main.cloudBGActive;
        }


        // ---- 入侵 ----
        if (c.Invasion)
        {
            to.Main.invasionType = from.Main.invasionType;
            to.Main.invasionSize = from.Main.invasionSize;
            to.Main.invasionSizeStart = from.Main.invasionSizeStart;
            to.Main.invasionX = from.Main.invasionX;
            to.Main.invasionDelay = from.Main.invasionDelay;
        }

        // ---- 困难模式 ----
        if (c.HardMode) { to.Main.hardMode = from.Main.hardMode; }

        // ---- Boss / 事件进度 ----
        if (c.BossProgress)
        {
            to.NPC.downedSlimeKing = from.NPC.downedSlimeKing;
            to.NPC.downedBoss1 = from.NPC.downedBoss1;
            to.NPC.downedBoss2 = from.NPC.downedBoss2;
            to.NPC.downedBoss3 = from.NPC.downedBoss3;
            to.NPC.downedQueenBee = from.NPC.downedQueenBee;
            to.NPC.downedMechBoss1 = from.NPC.downedMechBoss1;
            to.NPC.downedMechBoss2 = from.NPC.downedMechBoss2;
            to.NPC.downedMechBoss3 = from.NPC.downedMechBoss3;
            to.NPC.downedMechBossAny = from.NPC.downedMechBossAny;
            to.NPC.downedPlantBoss = from.NPC.downedPlantBoss;
            to.NPC.downedGolemBoss = from.NPC.downedGolemBoss;
            to.NPC.downedFishron = from.NPC.downedFishron;
            to.NPC.downedEmpressOfLight = from.NPC.downedEmpressOfLight;
            to.NPC.downedQueenSlime = from.NPC.downedQueenSlime;
            to.NPC.downedDeerclops = from.NPC.downedDeerclops;
            to.NPC.downedAncientCultist = from.NPC.downedAncientCultist;
            to.NPC.downedMoonlord = from.NPC.downedMoonlord;
            to.NPC.downedHalloweenKing = from.NPC.downedHalloweenKing;
            to.NPC.downedHalloweenTree = from.NPC.downedHalloweenTree;
            to.NPC.downedChristmasIceQueen = from.NPC.downedChristmasIceQueen;
            to.NPC.downedChristmasSantank = from.NPC.downedChristmasSantank;
            to.NPC.downedChristmasTree = from.NPC.downedChristmasTree;
            to.NPC.downedTowerSolar = from.NPC.downedTowerSolar;
            to.NPC.downedTowerVortex = from.NPC.downedTowerVortex;
            to.NPC.downedTowerNebula = from.NPC.downedTowerNebula;
            to.NPC.downedTowerStardust = from.NPC.downedTowerStardust;
            to.NPC.downedGoblins = from.NPC.downedGoblins;
            to.NPC.downedPirates = from.NPC.downedPirates;
            to.NPC.downedMartians = from.NPC.downedMartians;
            to.NPC.downedFrost = from.NPC.downedFrost;
        }

        // ---- 渔夫任务（世界级：当前委托 / 是否完成）----
        if (c.AnglerQuest)
        {
            to.Main.anglerQuest = from.Main.anglerQuest;
            to.Main.anglerQuestFinished = from.Main.anglerQuestFinished;
        }

        // ---- 已解救 NPC ----
        if (c.SavedNpc)
        {
            to.NPC.savedGoblin = from.NPC.savedGoblin;
            to.NPC.savedWizard = from.NPC.savedWizard;
            to.NPC.savedMech = from.NPC.savedMech;
            to.NPC.savedAngler = from.NPC.savedAngler;
            to.NPC.savedStylist = from.NPC.savedStylist;
            to.NPC.savedTaxCollector = from.NPC.savedTaxCollector;
            to.NPC.savedGolfer = from.NPC.savedGolfer;
            to.NPC.savedBartender = from.NPC.savedBartender;
        }

        if (log)
        {
            TShock.Log.Info($"[WorldChainSync] 世界状态已同步 {from.Name} -> {to.Name}"
                + $" (时间 {(from.Main.dayTime ? "白天" : "夜晚")} {from.Main.time:F0}"
                + $"，{(from.Main.raining ? "下雨" : "晴")}"
                + $"，骷髅王={(from.NPC.downedBoss3 ? "已击败" : "未击败")}"
                + $"，困难模式={(from.Main.hardMode ? "是" : "否")}"
                + $"，分组=时间{(c.Time ? "✓" : "✗")} 天气{(c.Weather ? "✓" : "✗")} 血月{(c.BloodMoon ? "✓" : "✗")} 日食{(c.Eclipse ? "✓" : "✗")} 南瓜{(c.PumpkinMoon ? "✓" : "✗")} 霜月{(c.SnowMoon ? "✓" : "✗")} 史莱姆雨{(c.SlimeRain ? "✓" : "✗")} 渔夫{(c.AnglerQuest ? "✓" : "✗")} 入侵{(c.Invasion ? "✓" : "✗")} 进度{(c.BossProgress ? "✓" : "✗")} NPC{(c.SavedNpc ? "✓" : "✗")} 困难{(c.HardMode ? "✓" : "✗")})");
        }
    }
}
