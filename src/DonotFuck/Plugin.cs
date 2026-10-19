using System.Collections.Immutable;
using System.Text.RegularExpressions;
using TShockAPI;
using UnifierTSL;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using TrProtocol.NetPackets.Modules;
using UnifierTSL.Plugins;

namespace DonotFuck;

[PluginMetadata("DonotFuck", "3.2.4", "Cai 羽学", "当玩家聊天有敏感词时用*号代替该词")]
public sealed class DonotFuckPlugin : BasePlugin
{
    private static DonotFuckConfig _config = new();
    private static string _configPath = "";
    private static string _logDir = "";
    private static readonly object LogGate = new();

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
        _logDir = Path.Combine(configRegistrar.Directory, "logs");
        _config = DonotFuckConfig.Load(_configPath);

        UnifierApi.EventHub.Chat.ChatEvent.Register(OnChat, HandlerPriority.Normal);
        NetPacketHandler.Register<NetTextModule>(OnTextPacket, HandlerPriority.VeryHigh);
        HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage += OnBroadcast;
        Commands.ChatCommands.Add(new Command("donotfuck.use", Command, "df"));
        TShock.Log.Info($"[DonotFuck] 已加载（{_config.DirtyWords.Count} 个敏感词）");
    }

    public override ValueTask DisposeAsync(bool disposing)
    {
        if (disposing)
        {
            UnifierApi.EventHub.Chat.ChatEvent.UnRegister(OnChat);
            NetPacketHandler.UnRegister<NetTextModule>(OnTextPacket);
            HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessage -= OnBroadcast;
        }
        return ValueTask.CompletedTask;
    }

    private static void OnChat(ref ValueEventArgs<ChatEvent> args)
    {
        try
        {
        ChatEvent e = args.Content;
        TSPlayer? player = e.Who >= 0 && e.Who < TShock.Players.Length ? TShock.Players[e.Who] : null;
        if (player is null) { return; }
        if (player.HasPermission("donotfuck.admin") || player.Group?.Name == "owner") { return; }

        string text = e.Text;
        if (string.IsNullOrEmpty(text)) { return; }
        if (text.StartsWith(TShock.Config.GlobalSettings.CommandSpecifier) || text.StartsWith(TShock.Config.GlobalSettings.CommandSilentSpecifier)) { return; }

        string replaced = Redact(text, out List<string> hits);
        if (hits.Count == 0) { return; }

        args.Content.Text = replaced;
        WriteLog(player.Name, hits);
        }
        catch (Exception ex) { TShock.Log.Error("[DonotFuck] chat 异常: " + ex); }
    }

    /// <summary>包级拦截：ChatHandler 之前改写客户端发来的聊天文本（VeryHigh=10 &lt; ChatHandler 的 11）。</summary>
    private static void OnTextPacket(ref ReceivePacketEvent<NetTextModule> args)
    {
        try
        {
            TextC2S? c2s = args.Packet.TextC2S;
            if (c2s is null || string.IsNullOrEmpty(c2s.Text)) { return; }

            TSPlayer? player = args.Who >= 0 && args.Who < TShock.Players.Length ? TShock.Players[args.Who] : null;
            if (player is null) { return; }
            if (player.HasPermission("donotfuck.admin") || player.Group?.Name == "owner") { return; }

            string replaced = Redact(c2s.Text, out List<string> hits);
            if (hits.Count == 0) { return; }
            c2s.Text = replaced;
            WriteLog(player.Name, hits);
        }
        catch (Exception ex) { TShock.Log.Error("[DonotFuck] packet 异常: " + ex); }
    }

    /// <summary>兜底：ChatEvent 没触发时，直接在广播出口改写（不区分管理员）。</summary>
    private static void OnBroadcast(Terraria.Chat.ChatHelperSystemContext sender, HookEvents.Terraria.Chat.ChatHelper.BroadcastChatMessageEventArgs args)
    {
        string text = args.text?.ToString() ?? "";
        if (text.Length == 0) { return; }

        TSPlayer? speaker = null;
        for (int i = 0; i < TShock.Players.Length; i++)
        {
            TSPlayer? p = TShock.Players[i];
            if (p is null || !p.Active || string.IsNullOrEmpty(p.Name)) { continue; }
            if (text.Contains(p.Name, StringComparison.Ordinal) && (speaker is null || p.Name.Length > speaker.Name.Length)) { speaker = p; }
        }
        if (speaker is null) { return; }
        if (speaker.HasPermission("donotfuck.admin") || speaker.Group?.Name == "owner") { return; }

        string replaced = Redact(text, out List<string> hits);
        if (hits.Count == 0) { return; }
        args.text = Terraria.Localization.NetworkText.FromLiteral(replaced);
        WriteLog(speaker.Name, hits);
    }

    private static string Redact(string text, out List<string> hits)
    {
        hits = [];
        string result = text;
        foreach (string word in _config.DirtyWords)
        {
            if (string.IsNullOrEmpty(word) || !result.Contains(word, StringComparison.OrdinalIgnoreCase)) { continue; }
            result = Regex.Replace(result, Regex.Escape(word), new string('*', word.Length), RegexOptions.IgnoreCase);
            hits.Add(word);
        }
        return result;
    }

    private static void WriteLog(string player, List<string> hits)
    {
        if (!_config.EnableLog) { return; }
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(_logDir);
                string file = Path.Combine(_logDir, $"DonotFuck-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"[{DateTime.Now:u}] [{player}] {string.Join(",", hits)}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static void Command(CommandArgs args)
    {
        bool admin = args.Player is null || args.Player.HasPermission("donotfuck.admin");
        if (args.Parameters.Count == 0) { Help(args, admin); return; }

        string sub = args.Parameters[0].ToLowerInvariant();
        switch (sub)
        {
            case "list":
            {
                int page = 1;
                if (args.Parameters.Count > 1) { int.TryParse(args.Parameters[1], out page); }
                ListWords(args, page);
                break;
            }
            case "add":
            case "del":
            {
                if (!admin) { Reply(args, "没有权限。", true); return; }
                if (args.Parameters.Count < 2) { Reply(args, $"用法：/df {sub} <词语>", true); return; }
                string word = args.Parameters[1];
                if (sub == "add")
                {
                    if (_config.DirtyWords.Contains(word, StringComparer.OrdinalIgnoreCase)) { Reply(args, "已在表中。", true); return; }
                    _config.DirtyWords.Add(word);
                    Reply(args, "已添加：" + word, false);
                }
                else
                {
                    if (!_config.DirtyWords.RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)).Equals(0)) { }
                    Reply(args, "已删除：" + word, false);
                }
                _config.Save(_configPath);
                break;
            }
            case "log":
                if (!admin) { Reply(args, "没有权限。", true); return; }
                _config.EnableLog = !_config.EnableLog;
                _config.Save(_configPath);
                Reply(args, $"敏感词记录已{(_config.EnableLog ? "开启" : "关闭")}", false);
                break;
            case "clear":
                if (!admin) { Reply(args, "没有权限。", true); return; }
                try { if (Directory.Exists(_logDir)) { foreach (string f in Directory.GetFiles(_logDir, "*.log")) { File.Delete(f); } } } catch { }
                Reply(args, "日志已清空。", false);
                break;
            default:
                Help(args, admin);
                break;
        }
    }

    private static void ListWords(CommandArgs args, int page)
    {
        List<string> words = _config.DirtyWords.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        int size = Math.Max(1, _config.PageSize);
        int pages = Math.Max(1, (int)Math.Ceiling(words.Count / (double)size));
        page = Math.Clamp(page, 1, pages);
        int start = (page - 1) * size;
        IEnumerable<string> line = words.Skip(start).Take(size).Select((w, i) => $"{start + i + 1}. {w}");
        Reply(args, $"《敏感词》第 {page}/{pages} 页：{string.Join("、", line)}", false);
    }

    private static void Help(CommandArgs args, bool admin)
    {
        Reply(args, "/df list [页] — 查看敏感词", false);
        if (admin)
        {
            Reply(args, "/df add|del <词语> — 增删", false);
            Reply(args, "/df log — 开关记录", false);
            Reply(args, "/df clear — 清空日志", false);
        }
    }

    private static void Reply(CommandArgs args, string text, bool error)
    {
        if (args.Player is not null)
        {
            if (error) { args.Player.SendErrorMessage(text); } else { args.Player.SendInfoMessage(text); }
        }
        else { TShock.Log.Info(text); }
    }
}
