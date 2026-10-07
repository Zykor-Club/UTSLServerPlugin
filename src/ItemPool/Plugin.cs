using System.Collections.Immutable;
using System.Reflection;
using TShockAPI;
using UnifierTSL.Plugins;

namespace ItemPool;

/// <summary>
/// ItemPool 物品自选池插件（UTSL 版）
///
/// 与 TShock 版的差异：
///   - 入口从 TerrariaPlugin + [ApiVersion] 改为 BasePlugin + [PluginMetadata]
///   - Initialize()/Dispose(bool) 改为 InitializeAsync()/DisposeAsync(bool)
///   - 配置目录改用 UTSL 的 IPluginConfigRegistrar.Directory（config/ItemPool/）
///   - 数据库访问从 TShock 的 Query/QueryReader 改为 linq2db（见 Database.cs）
///   - 命令注册方式不变（Commands.ChatCommands.Add 在 UTSL 中仍然保留）
/// </summary>
[PluginMetadata("ItemPool", "2026.7.23.0", "星梦XM", "物品自选池插件，让玩家从预设物品池中自选领取物品")]
public class Plugin : BasePlugin
{
    /// <summary>必须排在 TShock 之后，否则 TShock.DB / TShock.Groups 之类还没准备好</summary>
    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        // 等 TShock 初始化完成（TShock.DB 等在这里才可用）
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock")
            {
                await initInfo.InitializationTask;
            }
        }

        // 配置目录：config/ItemPool/
        ItemPoolConfig.Initialize(configRegistrar.Directory);
        ItemPoolConfig.Read();

        // 建表
        ItemPoolDatabase.Initialize();

        // 注册主命令 /xz，通过路由分发到各子命令
        Commands.ChatCommands.Add(new Command("xz.use", ItemPoolCommands.XzRoute, "xz", "选择")
        {
            HelpText = "物品自选池：/xz 查看帮助"
        });
    }

    public override ValueTask DisposeAsync(bool isDisposing)
    {
        if (isDisposing)
        {
            // 批量移除本程序集注册的所有命令
            Assembly asm = Assembly.GetExecutingAssembly();
            Commands.ChatCommands.RemoveAll(c =>
                c.CommandDelegate.Method?.DeclaringType?.Assembly == asm);
        }

        return base.DisposeAsync(isDisposing);
    }
}
