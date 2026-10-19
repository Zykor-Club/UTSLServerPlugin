using System.Collections.Immutable;
using Rests;
using Terraria;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace CGive;

public sealed class Warehouse
{
    public int Stack { get; set; }
    public int NetID { get; set; }
    public int Prefix { get; set; }

    public Warehouse(int stack, int netId, int prefix = 0)
    {
        Stack = stack;
        NetID = netId;
        Prefix = prefix;
    }
}

[PluginMetadata("CGive", "1.0.1", "Leader, 肝帝熙恩", "离线give")]
public sealed class CGivePlugin : BasePlugin
{
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

        CGiveDb.Init();
        PlayerHooks.PlayerPostLogin += OnLogin;
        Commands.ChatCommands.Add(new Command("cgive.admin", Command, "cgive"));
        TShock.RestApi.Register(new SecureRestCommand("/getWarehouse", GetWarehouse, RestPermissions.restapi));
        TShock.Log.Info("[CGive] 已加载（数据库表 CGive / Given）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { PlayerHooks.PlayerPostLogin -= OnLogin; }
        return ValueTask.CompletedTask;
    }

    private static void OnLogin(PlayerPostLoginEventArgs args)
    {
        TSPlayer? p = args.Player;
        if (p is null) { return; }

        TSPlayer target = p;
        Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 15; i++)
                {
                    if (target.Active && target.GetCurrentServer() is not null) { break; }
                    await Task.Delay(200);
                }
                ExecuteForPlayer(target);
            }
            catch (Exception ex) { TShock.Log.Error("[CGive] 登录执行失败: " + ex); }
        });
    }

    private static void ExecuteForPlayer(TSPlayer player)
    {
        if (!player.Active) { return; }

        foreach (CgiveRow row in CGiveDb.ForPlayer(player.Name))
        {
            if (row.who == "-1")
            {
                if (!CGiveDb.IsGiven(player.Name, row.ID) && ExecuteOnLogin(row, player))
                {
                    CGiveDb.AddGiven(player.Name, row.ID);
                }
            }
            else if (string.Equals(row.who, player.Name, StringComparison.OrdinalIgnoreCase) && ExecuteOnLogin(row, player))
            {
                CGiveDb.Delete(row.ID);
            }
        }
    }

    private static bool ExecuteOnLogin(CgiveRow row, TSPlayer target)
    {
        string cmd = row.cmd.Replace("{name}", target.Name);
        return Run(row.executer, target, cmd);
    }

    private static bool Run(string executerName, TSPlayer target, string cmd)
    {
        if (string.Equals(executerName, "server", StringComparison.OrdinalIgnoreCase))
        {
            ServerContext? server = target.GetCurrentServer();
            if (server is null) { return false; }
            TShockConsoleCommand.TryExecute(cmd, out _, server);
            return true;
        }

        TSPlayer? executer = TShock.Players.FirstOrDefault(x => x is not null && x.Active &&
            string.Equals(x.Name, executerName, StringComparison.OrdinalIgnoreCase));
        if (executer is null) { return false; }
        ServerContext? execServer = executer.GetCurrentServer();
        if (execServer is null) { return false; }
        Commands.HandleCommand(new CommandExecutor(execServer, (byte)executer.Index), cmd);
        return true;
    }

    /// <summary>对应上游 CGive.Execute：在线目标/全体模式立即执行，否则保存。</summary>
    private static bool ExecuteNow(string executerName, string cmd, string who)
    {
        if (who == "-1")
        {
            TSPlayer? executer = string.Equals(executerName, "server", StringComparison.OrdinalIgnoreCase)
                ? null
                : TShock.Players.FirstOrDefault(p => p is not null && p.Active &&
                      p.Name.Equals(executerName, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(executerName, "server", StringComparison.OrdinalIgnoreCase) && executer is null) { return false; }

            int id = CGiveDb.Add(executerName, cmd, who);
            for (int i = 0; i < TShock.Players.Length; i++)
            {
                TSPlayer? p = TShock.Players[i];
                if (p is null || !p.Active) { continue; }
                Run(executerName, p, cmd.Replace("{name}", p.Name));
                CGiveDb.AddGiven(p.Name, id);
            }
            return true;
        }

        TSPlayer? target = TShock.Players.FirstOrDefault(p => p is not null && p.Active &&
            p.Name.Equals(who, StringComparison.OrdinalIgnoreCase));
        if (target is null) { return false; }
        if (!string.Equals(executerName, "server", StringComparison.OrdinalIgnoreCase) &&
            !TShock.Players.Any(p => p is not null && p.Active && p.Name.Equals(executerName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        return Run(executerName, target, cmd.Replace("{name}", target.Name));
    }

    private static void Command(CommandArgs args)
    {
        if (args.Parameters.Count == 0) { Help(args); return; }

        switch (args.Parameters[0].ToLowerInvariant())
        {
            case "reset":
                CGiveDb.Clear();
                Reply(args, "成功删除所有数据");
                break;

            case "del":
            {
                if (args.Parameters.Count < 2 || !int.TryParse(args.Parameters[1], out int id))
                {
                    Reply(args, "用法: /cgive del [id]");
                    return;
                }
                CGiveDb.Delete(id);
                Reply(args, "已执行删除");
                break;
            }

            case "list":
                foreach (CgiveRow row in CGiveDb.All())
                {
                    Reply(args, $"执行者:{row.executer} 被执行者:{row.who} 命令:{row.cmd} id:{row.ID}");
                }
                break;

            case "add":
            {
                if (args.Parameters.Count < 4)
                {
                    Reply(args, "用法: /cgive add [执行者] [被执行者] [命令]");
                    return;
                }
                string executer = args.Parameters[1];
                string who = args.Parameters[2];
                string cmd = string.Join(' ', args.Parameters.Skip(3));

                if (!ExecuteNow(executer, cmd, who))
                {
                    CGiveDb.Add(executer, cmd, who);
                    Reply(args, "命令已保存");
                }
                else
                {
                    Reply(args, "命令执行成功！");
                }
                break;
            }

            default:
                Reply(args, $"未知子命令: {args.Parameters[0]}，输入 /cgive 查看帮助");
                break;
        }
    }

    private static void Help(CommandArgs args)
    {
        Reply(args, "/cgive add [执行者] [被执行者] [命令]");
        Reply(args, "  执行者: Server 或 玩家名");
        Reply(args, "  被执行者: 玩家名 或 -1(所有玩家)");
        Reply(args, "/cgive list - 列出所有离线命令");
        Reply(args, "/cgive del [id] - 删除指定 id 的离线命令");
        Reply(args, "/cgive reset - 重置所有数据");
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendInfoMessage(text); } else { TShock.Log.Info(text); }
    }

    private static object GetWarehouse(RestRequestArgs args)
    {
        string text = args.Parameters["name"];
        if (string.IsNullOrWhiteSpace(text) || TShock.UserAccounts.GetUserAccountsByName(text).Count == 0)
        {
            return new RestObject { Error = $"没有找到注册名为{text}的账户" };
        }

        List<Warehouse> list = [];
        string specifier = Commands.Specifier;
        foreach (CgiveRow row in CGiveDb.ForWho(text))
        {
            string[] array = row.cmd.Split(' ');
            if (array.Length < 3 || (array[0].ToLower() != specifier + "give" && array[0].ToLower() != specifier + "g"))
            {
                continue;
            }

            int result = 0;
            if (array.Length >= 4 && !int.TryParse(array[3], out result)) { continue; }

            List<Item> items = TShockAPI.Utils.GetItemByIdOrName(array[1]);
            if (items.Count == 0) { continue; }
            Item item = items[0];

            if (array.Length == 3) { list.Add(new Warehouse(item.maxStack, item.type)); continue; }
            if (array.Length == 4) { list.Add(new Warehouse(result, item.type)); continue; }
            if (array.Length == 5)
            {
                List<int> prefix = TShockAPI.Utils.GetPrefixByIdOrName(array[4]);
                if (prefix.Count != 0) { list.Add(new Warehouse(result, item.type, prefix[0])); }
            }
        }

        return list.Count == 0
            ? new RestObject { Error = "该玩家没有仓库" }
            : new RestObject("200") { { "response", "获取成功" }, { "data", list } };
    }
}
