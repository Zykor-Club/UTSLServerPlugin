using System.Collections.Immutable;
using AutoResetPlus.Core;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Plugins;

namespace AutoResetPlus;

/// <summary>
/// AutoResetPlus（UTSL 多世界版）。
///
/// 原插件是 TShock 单世界重置（进程内 WorldGen.CreateNewWorld → WorldFile.LoadWorld）。
/// 但本服务器的三个世界是【同一张大地图切出来的三片】，各重置各的会让接缝立刻断裂，
/// 所以这里的重置必须是【整链重置】：
///
///   ① 生成一张新的源大地图（12800 宽，新种子）
///   ② 用 UTSLStitchTool 重新切成三片（片宽自动对齐 200 的倍数）
///   ③ 替换 West / Dev / East
///   ④ 重启服务器让新切片生效
///
/// 参数（源地图宽度 / 切片数 / 重叠量 / 切片间距）都写在 config/AutoResetPlus/MultiWorld.json。
/// </summary>
[PluginMetadata("AutoResetPlus", "1.0.0", "Eustia & cc04 & Leader & 棱镜 & Cai & 肝帝熙恩 & 星梦",
    "重置插件增强版：整链重置多世界（生成源地图 → 切分 → 部署 → 重启）")]
public sealed class AutoResetPlusPlugin : BasePlugin
{
    private string _configDir = "";
    private ChainResetter _resetter = null!;

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

        _configDir = configRegistrar.Directory;   // 这个目录本身已经带插件名了，不要重复拼
        Directory.CreateDirectory(_configDir);
        _resetter = new ChainResetter(_configDir);

        // 如果上一次重启留下了「待部署」标记，说明源地图刚生成好 —— 继续切分部署
        _ = Task.Run(() => _resetter.ResumeIfPendingAsync());

        Commands.ChatCommands.Add(new Command("reset.admin", OnReset, "reset", "重置世界"));
        Commands.ChatCommands.Add(new Command("reset.admin", OnResetData, "resetdata", "重置数据"));
        Commands.ChatCommands.Add(new Command("reset.admin", OnSettings, "rs", "重置设置"));
        Commands.ChatCommands.Add(new Command("reset.admin", OnStatus, "rsinfo", "重置状态"));

        TShock.Log.Info("[AutoResetPlus] 已加载（整链重置模式）。用 /reset 重置全部世界，/rs info 查看预设。");

        // 无客户端时的自测钩子：AUTORESET_TEST = info | seed:<种子> | reset
        string? test = Environment.GetEnvironmentVariable("AUTORESET_TEST");
        TShock.Log.Info("[AutoResetPlus] 自测环境变量 AUTORESET_TEST = " + (test ?? "(未设置)") + "，配置目录 = " + _configDir);
        if (!string.IsNullOrEmpty(test))
        {
            // ★ 立刻从【当前进程】的环境里清掉它。
            //   否则它会随子进程继承下去，重启后又触发一次 → 无限重置循环（实测踩过）。
            //   Process 作用域只影响本进程及其子进程，不会污染系统环境变量。
            Environment.SetEnvironmentVariable("AUTORESET_TEST", null, EnvironmentVariableTarget.Process);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                TShock.Log.Info("[AutoResetPlus][TEST] 执行自测: " + test);
                try
                {
                    if (test.StartsWith("seed:", StringComparison.OrdinalIgnoreCase))
                    {
                        _resetter.HandleSettings(null, ["seed", test.Substring(5)]);
                    }
                    else if (test.Equals("reset", StringComparison.OrdinalIgnoreCase))
                    {
                        _resetter.StartReset(null, []);
                    }
                    _resetter.PrintStatus(null);
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[AutoResetPlus][TEST] 自测异常: " + ex);
                }
            });
        }
    }

    private void OnReset(CommandArgs args)
    {
        if (args.Parameters.Count > 0 && args.Parameters[0] is "info")
        {
            OnStatus(args);
            return;
        }
        _resetter.StartReset(args.Player, args.Parameters);
    }

    private void OnResetData(CommandArgs args)
    {
        _resetter.ResetDataOnly(args.Player);
    }

    private void OnSettings(CommandArgs args)
    {
        _resetter.HandleSettings(args.Player, args.Parameters);
    }

    private void OnStatus(CommandArgs args)
    {
        _resetter.PrintStatus(args.Player);
    }
}
