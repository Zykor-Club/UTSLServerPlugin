using Terraria;
using TShockAPI;
using UnifierTSL.Servers;

namespace EdgeStitch.Core;

/// <summary>
/// 跨世界状态同步。
///
/// UTSL 的每个世界都有自己独立的 Main / NPC 上下文（源码里没有任何同步逻辑），
/// 所以时间、天气、世界进度这些必须由我们自己搬。
///
/// 玩家侧的 buff/debuff、血量、背包【不需要】同步：UTSL 换乘迁移的是同一个 Player 对象。
/// </summary>
public static class WorldStateSync
{
    /// <summary>把 from 世界的世界级状态整体拷到 to 世界。</summary>
    public static void CopyWorldState(ServerContext from, ServerContext to, bool log = false)
    {
        // ---- 时间 ----
        to.Main.time = from.Main.time;
        to.Main.dayTime = from.Main.dayTime;
        to.Main.moonPhase = from.Main.moonPhase;
        to.Main.bloodMoon = from.Main.bloodMoon;
        to.Main.eclipse = from.Main.eclipse;
        to.Main.fastForwardTimeToDawn = from.Main.fastForwardTimeToDawn;
        to.Main.fastForwardTimeToDusk = from.Main.fastForwardTimeToDusk;
        to.Main.sundialCooldown = from.Main.sundialCooldown;
        to.Main.numStars = from.Main.numStars;
        // [上下文无此字段] to.Main.bloodMoonCount = from.Main.bloodMoonCount;
        to.Main.moonType = from.Main.moonType;

        // ---- 天气 ----
        to.Main.raining = from.Main.raining;
        to.Main.rainTime = from.Main.rainTime;
        to.Main.maxRaining = from.Main.maxRaining;
        // [上下文无此字段] to.Main.windyDay = from.Main.windyDay;
        to.Main.windSpeedCurrent = from.Main.windSpeedCurrent;
        to.Main.windSpeedTarget = from.Main.windSpeedTarget;
        to.Main.numClouds = from.Main.numClouds;
        to.Main.slimeRain = from.Main.slimeRain;
        to.Main.slimeRainTime = from.Main.slimeRainTime;
        to.Main.cloudBGActive = from.Main.cloudBGActive;

        // ---- 难度 / 世界模式 ----
        to.Main.hardMode = from.Main.hardMode;
        to.Main.GameMode = from.Main.GameMode;

        // ---- 入侵 ----
        to.Main.invasionType = from.Main.invasionType;
        to.Main.invasionSize = from.Main.invasionSize;
        to.Main.invasionSizeStart = from.Main.invasionSizeStart;
        to.Main.invasionX = from.Main.invasionX;
        to.Main.invasionDelay = from.Main.invasionDelay;

        // ---- 世界进度（Boss 旗标）----
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
        // [上下文无此字段] to.NPC.downedDD2EventAnyDifficulty = from.NPC.downedDD2EventAnyDifficulty;

        // 各类"已解救 NPC"旗标
        to.NPC.savedGoblin = from.NPC.savedGoblin;
        to.NPC.savedWizard = from.NPC.savedWizard;
        to.NPC.savedMech = from.NPC.savedMech;
        to.NPC.savedAngler = from.NPC.savedAngler;
        to.NPC.savedStylist = from.NPC.savedStylist;
        to.NPC.savedTaxCollector = from.NPC.savedTaxCollector;
        to.NPC.savedGolfer = from.NPC.savedGolfer;
        to.NPC.savedBartender = from.NPC.savedBartender;

        // 矿石层级（影响祭坛/矿物生成判定）
        // [上下文无此字段] to.NPC.savedOreTiersCopper = from.NPC.savedOreTiersCopper;
        // [上下文无此字段] to.NPC.savedOreTiersIron = from.NPC.savedOreTiersIron;
        // [上下文无此字段] to.NPC.savedOreTiersSilver = from.NPC.savedOreTiersSilver;
        // [上下文无此字段] to.NPC.savedOreTiersGold = from.NPC.savedOreTiersGold;
        // [上下文无此字段] to.NPC.savedOreTiersCobalt = from.NPC.savedOreTiersCobalt;
        // [上下文无此字段] to.NPC.savedOreTiersMythril = from.NPC.savedOreTiersMythril;
        // [上下文无此字段] to.NPC.savedOreTiersAdamantite = from.NPC.savedOreTiersAdamantite;

        TShock.Log.Info($"[EdgeStitch] 世界状态已同步 {from.Name} -> {to.Name}"
            + $" (时间 {(from.Main.dayTime ? "白天" : "夜晚")} {from.Main.time:F0}"
            + $"，{(from.Main.raining ? "下雨" : "晴")}"
            + $"，骷髅王={(from.NPC.downedBoss3 ? "已击败" : "未击败")}"
            + $"，困难模式={(from.Main.hardMode ? "是" : "否")})");
    }

    /// <summary>
    /// 周期性统一：挑一个"有玩家的世界"作为权威，把状态推给链上其它世界，
    /// 否则各世界的时间/天气会各自漂移。
    /// </summary>
    public static void UnifyChain(IReadOnlyList<ServerContext> chainWorlds)
    {
        if (chainWorlds.Count < 2)
        {
            return;
        }

        ServerContext? master = null;
        foreach (ServerContext w in chainWorlds)
        {
            if (w.Main.player.Any(p => p is not null && p.active))
            {
                master = w;
                break;
            }
        }

        master ??= chainWorlds[0];

        foreach (ServerContext w in chainWorlds)
        {
            if (ReferenceEquals(w, master))
            {
                continue;
            }
            CopyWorldState(master, w);
        }
    }
}
