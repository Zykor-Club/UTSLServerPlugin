using System.Collections.Immutable;
using Terraria;
using Terraria.ID;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Extensions;
using UnifierTSL.Plugins;
using UnifierTSL.Servers;

namespace AutoAirItem;

[PluginMetadata("AutoAirItem", "1.2.8", "羽学", "自动垃圾桶帮助玩家清理自身垃圾")]
public sealed class AutoAirPlugin : BasePlugin
{
    private static AutoAirConfig _config = new();
    private static string _configPath = "";
    private static readonly Dictionary<int, long> LastScan = new();

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
        _config = AutoAirConfig.Load(_configPath);
        AutoTrashDb.Init();
        _config.Players = AutoTrashDb.LoadAll();

        UnifierApi.EventHub.Game.PostUpdate.Register(OnTick, HandlerPriority.Normal);
        Commands.ChatCommands.Add(new Command("autoair.use", AirCommand, "air", "垃圾"));
        Commands.ChatCommands.Add(new Command("autoair.admin", ResetCommand, "airreset", "重置垃圾桶"));
        TShock.Log.Info("[AutoAirItem] 已加载");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing) { UnifierApi.EventHub.Game.PostUpdate.UnRegister(OnTick); }
        return ValueTask.CompletedTask;
    }

    private static void OnTick(ref ReadonlyNoCancelEventArgs<ServerEvent> args)
    {
        if (!_config.Enable) { return; }
        ServerContext server = args.Content.Server;
        long now = Environment.TickCount64;

        for (int i = 0; i < TShock.Players.Length; i++)
        {
            TSPlayer? p = TShock.Players[i];
            if (p is null || !p.Active || !p.IsLoggedIn || !p.HasPermission("autoair.use")) { continue; }
            if (!ReferenceEquals(p.GetCurrentServer(), server)) { continue; }
            if (LastScan.TryGetValue(p.Index, out long last) && now - last < 500) { continue; }
            LastScan[p.Index] = now;
            Scan(server, p);
        }
    }

    private static void Scan(ServerContext server, TSPlayer p)
    {
        AutoAirConfig.PlayerData data = _config.For(p.Name);
        if (!data.Enabled) { return; }

        Player t = p.TPlayer;
        for (int i = 0; i < t.inventory.Length; i++)
        {
            Item inv = t.inventory[i];
            Item trash = t.trashItem;
            Item selected = t.inventory[t.selectedItem];

            // 上游语义：垃圾桶或当前格命中排除表 -> 整个扫描直接返回
            if (Array.IndexOf(_config.Exclude, trash.type) >= 0 || Array.IndexOf(_config.Exclude, inv.type) >= 0)
            {
                return;
            }

            if (data.Auto && !trash.IsAir)
            {
                if (!data.Types.Contains(trash.type))
                {
                    data.Types.Add(trash.type);
                    AutoTrashDb.Save(data);
                }
                else
                {
                    if (data.Mess)
                    {
                        string name = Lang.GetItemNameValue(trash.type);
                        p.SendMessage($"已从垃圾桶移除:[c/EC73B9:{trash.stack}]个[c/FF5C5F:{name}]|[c/92C5EC:返还]: [c/A1D4C2:/air del {trash.type}]", 255, 246, 158);
                    }
                    UpDict(data.Deleted, trash.type, trash.stack);
                    AutoTrashDb.Save(data);
                    trash.TurnToAir(server);
                    Sync(p, PlayerItemSlotID.TrashItem);
                }
            }

            if (data.Types.Contains(inv.type) && inv.type != selected.type)
            {
                if (data.Mess)
                {
                    string name = Lang.GetItemNameValue(inv.type);
                    p.SendMessage($"已从背包移除:[c/EC73B9:{inv.stack}]个[c/FF5C5F:{name}]|[c/92C5EC:返还]: [c/A1D4C2:/air del {inv.type}]", 255, 246, 158);
                }
                UpDict(data.Deleted, inv.type, inv.stack);
                AutoTrashDb.Save(data);
                inv.TurnToAir(server);
                Sync(p, PlayerItemSlotID.Inventory0 + i);
            }
        }
    }

    private static void Sync(TSPlayer p, int slot)
    {
        try { p.SendData(TrProtocol.MessageID.PlayerSlot, "", p.Index, slot); } catch { }
    }

    public static void UpDict(Dictionary<int, int> delItem, int type, int stack)
    {
        if (delItem.ContainsKey(type)) { delItem[type] += stack; }
        else { delItem[type] = stack; }
    }

    private static void HelpCmd(TSPlayer p)
    {
        p.SendInfoMessage("【自动垃圾桶】指令菜单\n"
            + "/air —— 查看菜单\n"
            + "/airreset —— 清空所有玩家数据\n"
            + "/air on —— 开启/关闭垃圾桶\n"
            + "/air list —— 列出自己的垃圾桶\n"
            + "/air clear —— 清理垃圾桶\n"
            + "/air yes —— 将手持物品加入垃圾桶\n"
            + "/air auto —— 监听垃圾桶位格开关\n"
            + "/air mess —— 开启/关闭清理消息\n"
            + "/air ck <数量> —— 筛选垃圾数量超过此值的玩家\n"
            + "/air add|del <名字> —— 添加/移出垃圾桶物品");
    }

    private static void AirCommand(CommandArgs args)
    {
        TSPlayer p = args.Player;
        if (p is null) { TShock.Log.Info("[AutoAirItem] 该指令需要玩家执行"); return; }
        if (!_config.Enable) { return; }

        AutoAirConfig.PlayerData data = _config.For(p.Name);

        if (args.Parameters.Count == 0)
        {
            HelpCmd(p);
            if (!data.Enabled) { p.SendSuccessMessage("请输入该指令开启 → [c/92C5EC:/air on]"); }
            else
            {
                p.SendSuccessMessage($"您的垃圾桶监听状态为：[c/F3F292:{data.Auto}]");
                p.SendSuccessMessage("输入 [c/C086DF:/air auto] 切换自动模式");
            }
            return;
        }

        if (args.Parameters.Count == 1)
        {
            switch (args.Parameters[0].ToLowerInvariant())
            {
                case "list":
                {
                    string list = data.Types.Count == 0 ? "（空）" : string.Join("、", data.Types.Select(x => $"{Lang.GetItemNameValue(x)}([c/92C5EC:{x}])"));
                    p.SendInfoMessage($"[{data.Name}的垃圾桶]\n{list}");
                    return;
                }
                case "on":
                    data.Enabled = !data.Enabled;
                    p.SendSuccessMessage(data.Enabled
                        ? $"玩家 [{p.Name}] 已[c/92C5EC:启用]自动垃圾桶功能。"
                        : $"玩家 [{p.Name}] 已[c/92C5EC:禁用]自动垃圾桶功能。");
                    AutoTrashDb.Save(data);
                    return;
                case "clear":
                    data.Types.Clear();
                    data.Deleted.Clear();
                    p.SendSuccessMessage($"已清理[c/92C5EC: {p.Name} ]的自动垃圾桶表");
                    AutoTrashDb.Save(data);
                    return;
                case "yes":
                {
                    Item held = p.TPlayer.inventory[p.TPlayer.selectedItem];
                    if (held.IsAir) { p.SendErrorMessage("手上没有物品。"); return; }
                    if (!data.Types.Contains(held.type)) { data.Types.Add(held.type); }
                    p.SendSuccessMessage($"手选物品 [c/92C5EC:{held.Name}] 已加入自动垃圾桶中! 脱手即清!");
                    AutoTrashDb.Save(data);
                    return;
                }
                case "auto":
                    data.Auto = !data.Auto;
                    p.SendSuccessMessage(data.Auto
                        ? $"玩家 [{p.Name}] 的垃圾桶位格监听功能已[c/92C5EC:启用]"
                        : $"玩家 [{p.Name}] 的垃圾桶位格监听功能已[c/92C5EC:禁用]");
                    AutoTrashDb.Save(data);
                    return;
                case "mess":
                    data.Mess = !data.Mess;
                    p.SendSuccessMessage(data.Mess
                        ? $"玩家 [{p.Name}] 的自动清理消息已[c/92C5EC:启用]"
                        : $"玩家 [{p.Name}] 的自动清理消息已[c/92C5EC:禁用]");
                    AutoTrashDb.Save(data);
                    return;
                default:
                    HelpCmd(p);
                    return;
            }
        }

        if (args.Parameters.Count == 2)
        {
            if (args.Parameters[0].Equals("ck", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(args.Parameters[1], out int num)) { CheckCmd(p, num); }
                return;
            }

            List<Item> items = TShockAPI.Utils.GetItemByIdOrName(args.Parameters[1]);
            if (items.Count == 0) { p.SendErrorMessage($"不存在该物品：{args.Parameters[1]}"); return; }
            if (items.Count > 1)
            {
                p.SendErrorMessage("匹配到多个物品：" + string.Join("、", items.Take(8).Select(i => $"{i.Name}({i.type})")));
                return;
            }
            Item item = items[0];

            switch (args.Parameters[0].ToLowerInvariant())
            {
                case "add":
                    if (data.Types.Contains(item.type)) { p.SendErrorMessage($"物品 [c/92C5EC:{item.Name}] 已在垃圾桶中!"); return; }
                    data.Types.Add(item.type);
                    p.SendSuccessMessage($"已成功将物品添加到垃圾桶: [c/92C5EC:{item.Name}]!");
                    AutoTrashDb.Save(data);
                    break;
                case "del":
                case "delete":
                case "remove":
                {
                    if (!data.Types.Contains(item.type)) { p.SendErrorMessage($"物品 {item.Name} 不在垃圾桶中!"); return; }
                    data.Deleted.TryGetValue(item.type, out int count);
                    p.SendSuccessMessage($"已从垃圾桶移出:[c/DEFF7D:{count}]个[c/92C5EC:{item.Name}]");
                    if (count > 0)
                    {
                        int max = item.maxStack <= 0 ? 9999 : item.maxStack;
                        for (int i = 0; i < count; i += max)
                        {
                            int give = Math.Min(max, count - i);
                            p.GiveItem(item.type, give, 0);
                        }
                    }
                    data.Deleted.Remove(item.type);
                    data.Types.Remove(item.type);
                    AutoTrashDb.Save(data);
                    break;
                }
                default:
                    HelpCmd(p);
                    break;
            }
        }
    }

    private static void CheckCmd(TSPlayer p, int num)
    {
        if (!_config.Enable) { return; }
        p.SendInfoMessage($"以下垃圾物品数量超过【[c/92C5EC:{num}]】的玩家");
        bool found = false;
        int index = 1;

        foreach (AutoAirConfig.PlayerData data in _config.Players)
        {
            List<(string Name, int Count)> items = data.Deleted
                .Where(kv => kv.Value >= num)
                .Select(kv => (Lang.GetItemNameValue(kv.Key), kv.Value))
                .ToList();
            if (items.Count == 0) { continue; }
            found = true;
            p.SendMessage($"[c/32CEB7:{index++}.][c/F3E83B:{data.Name}:] " + string.Join(", ", items.Select(x => $"{x.Name}([c/92C5EC:{x.Count}])")), 193, 223, 186);
        }

        if (!found) { p.SendMessage($"没有找到垃圾数量超过[c/92C5EC:{num}]的玩家", 193, 223, 186); }
    }

    private static void ResetCommand(CommandArgs args)
    {
        AutoTrashDb.Clear();
        _config.Players.Clear();
        Reply(args, "已清空所有玩家《自动垃圾桶》数据！");
    }

    private static void Reply(CommandArgs args, string text)
    {
        if (args.Player is not null) { args.Player.SendSuccessMessage(text); } else { TShock.Log.Info(text); }
    }
}
