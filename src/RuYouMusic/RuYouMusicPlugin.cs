using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rests;
using Terraria.Net.Sockets;
using TShockAPI;
using TShockAPI.Hooks;
using UnifierTSL.Events.Core;
using UnifierTSL.Events.Handlers;
using UnifierTSL.Plugins;

namespace RuYouMusic;

/// <summary>
/// RuYouMusic v1.0.0 —— 点歌机（服务端侧）。
///
/// 与客户端插件（自己写的那份）约定：
///   S→C 129 PROBE    探测客户端是否装了插件（原版客户端对该 id 无处理，安全忽略）
///   C→S  15 HELLO    客户端回包（魔数 RYM1）→ 服务端下发 SESSION 随机令牌
///   S→C  67 PLAY/STOP/VOLUME   只发给已握手的客户端（避免原版客户端误解析自定义载荷）
///   C→S  15 FINISHED / POSITION  带令牌，防伪造
///
///   帧：[长度:2 LE][类型:1][载荷:256]
///   载荷：[0..3]=RYM1  [4]=cmd  [5]=协议版本  [6..13]=token  [14..15]=保留  [16..]=数据
///   （收包侧 RawData 不含长度头，所以 RawData[0]=类型、RawData[1+载荷偏移]=字段）
///
/// REST：/music/status /music/clients /music/queue /music/lyrics /music/search
///       /music/enqueue /music/skip /music/stop /music/volume /music/remove /music/clear /music/config
/// 指令：/music（别名 点歌）search | add | list | skip | stop | vol | clients
/// </summary>
[PluginMetadata("RuYouMusic", "1.0.0", "星梦", "点歌机：搜索/队列/下发客户端播放/歌词（REST + 指令）")]
public sealed class RuYouMusicPlugin : BasePlugin
{
    // ===== 协议 =====
    private const byte IdProbe = 129;
    private const byte IdDown = 67;
    private const byte IdUp = 15;
    private const int FrameSize = 1024;   // ★ 原来是 260：歌名+歌手占掉后，直链只剩 ~160 字节，
                                       //   稍长的 URL 会被静默截断 → 客户端下到 400/403（"有一些歌能放、有一些失败"的元凶）
    private const int PayloadOffset = 16;      // 载荷内数据起点
    private const int FrameDataOffset = 3 + PayloadOffset;   // ★ 帧内数据起点：2 字节长度 + 1 字节类型，再 +16
                                                             //   （踩过坑：直接拿 PayloadOffset 写 frame 会整体提前 3 字节，
                                                             //    客户端按载荷偏移解析就全错位）
    private const byte Proto = 2;   // v2：PLAY 帧末尾追加直链 URL
    private static readonly byte[] Magic = { 0x52, 0x59, 0x4D, 0x31 };   // "RYM1"
    private const byte UpHello = 1, UpFinished = 3, UpPosition = 4, UpEvent = 5;
    private const byte DnProbe = 1, DnPlay = 2, DnStop = 3, DnVolume = 4, DnSession = 5, DnPrefetch = 6;

    // ★ 取链接口可插拔：把 apiBase 指向任何能出直链的服务即可（自建代理、带浏览器会话的转发、付费接口…）
    private static string _apiBase = "https://music-api.gdstudio.xyz/api.php";
    private static string _apiExtra = "";        // 追加到每个请求末尾的原始串（例如自己的 token 参数）
    private static string _apiHeaders = "";      // 形如 "Cookie=a=b;Referer=https://..."，用 ; 分隔，会加到每个请求上
    private static int _apiSign;                 // 0=不签名；1=按前端约定追加 &s=crc32(签名对象)
    // ★ 该接口只支持 netease / joox 等少数音源；网易云对 VIP/地区受限的歌会返回空直链，
    //   这时换 joox 用同一关键字再找，通常能拿到可用直链（不同 CDN）。
    private static string[] _sources = { "netease" };   // 实测 joox 当前返回空直链；netease 稳定，受限歌曲走「同名可播放版本」兜底
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    // ★ 直链解析缓存 + 全局节流：频繁请求会把公共接口打到 503（实测 12 连发就出现 HTML 错误页），
    //   而面板 check=1 搜索原来一条结果要 2 个请求、8 条结果就是 16 个请求 → 自己把自己限流了。
    private sealed class Resolved { public string Url = ""; public long DurationMs; public long AtMs; public bool Playable; }
    private static readonly ConcurrentDictionary<string, Resolved> ResolveCache = new();
    private static readonly SemaphoreSlim ApiGate = new(1, 1);
    private static long _lastApiMs;
    private static long _cacheHits;
    private static long _apiCalls;
    private const int ApiGapMs = 300;          // 两次接口请求之间至少间隔
    private const int UrlTtlMs = 15 * 60 * 1000;   // 直链有效期（网易云直链带时间戳，过期就得重解析）
    private const int NegTtlMs = 5 * 60 * 1000;    // 「取不到」也记 5 分钟，避免反复重试同一首

    // ===== 状态 =====
    private sealed class ClientState
    {
        public string Name = "";
        public byte Version;
        public byte[] Token = new byte[8];
        public long LastSeenMs;
        public long LastPosMs;
        public long LastPosAtMs;
        public long TrackId = -1;
        // ★ 客户端回传的播放过程事件（面板据此显示「谁没出声、为什么」）
        public byte LastEventKind;
        public long LastEventMs;
        public long LastEventTrackId = -1;
        public string LastEventMsg = "";
    }

    private sealed class Track
    {
        public long Id;                 // 对外/协议用的稳定 id（由 音源+远端id 哈希得来，跨会话一致 → 客户端缓存可复用）
        public string Source = "netease";   // 音源：netease / joox
        public string RemoteId = "";        // 音源内的原始 id（joox 是 24 位 base64，必须 URL 转义）
        public string UrlId = "";
        public string Album = "";           // 取直链专用 id（搜索结果里的 url_id，部分歌与 id 不同）
        public string Name = "";
        public string Artist = "";
        public long DurationMs;
        public string By = "";
        public string Url = "";
    }

    private sealed class LyricLine
    {
        public double Ms;
        public string Text = "";
    }

    private static readonly ConcurrentDictionary<int, ClientState> Clients = new();
    private static readonly List<Track> Queue = new();
    private static readonly List<LyricLine> Lyrics = new();

    /// <summary>最近失败记录（面板直接看，不用翻日志）。</summary>
    private sealed class FailRec
    {
        public long Ms; public int Who; public string Player = ""; public string Song = ""; public long TrackId = -1; public string Stage = ""; public string Reason = "";
    }
    private static readonly List<FailRec> Failures = new();

    private static void RecordFailure(int who, string player, string song, long trackId, string stage, string reason)
    {
        try
        {
            lock (Sync)
            {
                Failures.Add(new FailRec { Ms = NowMs, Who = who, Player = player, Song = song, TrackId = trackId, Stage = stage, Reason = reason });
                while (Failures.Count > 60) { Failures.RemoveAt(0); }
            }
            TShock.Log.Warning("[RuYouMusic] 失败: " + stage + " | " + player + " | " + song + " | " + reason);
        }
        catch { }
    }
    private static readonly object Sync = new();
    private static readonly object ApiLock = new();
    private static Track? Current;
    private static long CurrentStartedMs;
    private static int CurrentOffsetMs;
    private static int _inited;
    private static Timer? _tick;

    // ===== 配置 =====
    private static string? _configPath;
    private static bool _enabled = true;
    private static int _maxQueue = 20;
    private static int _searchCount = 5;
    private static int _probeSeconds = 15;
    private static bool _autoNext = true;
    private static int _volumePercent = 80;
    private static string _commandPermission = "ruyoumusic.use";

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1); }
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(string s)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in Encoding.UTF8.GetBytes(s ?? "")) { c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); }
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>按配置给请求追加签名（前端约定：url/lyric/pic 用 urlEncode(id)，search 用关键字）。</summary>
    private static string Sign(string subject) => _apiSign == 1 ? ("&s=" + Crc32(subject).ToString(CultureInfo.InvariantCulture)) : "";

    private static string Extra() => _apiExtra;

    /// <summary>由「音源 + 远端 id」算一个稳定的 64 位正数 id：协议与客户端缓存都用它。</summary>
    private static long StableId(string source, string remoteId)
    {
        ulong h = 1469598103934665603UL;
        string src = source + "|" + remoteId;
        foreach (char c in src) { h ^= c; h *= 1099511628211UL; }
        return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
    }

    // ============================================================
    // 生命周期
    // ============================================================

    public override Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        try { _configPath = Path.Combine(configRegistrar.Directory, "settings.json"); } catch { _configPath = null; }
        LoadSettings();

        if (Interlocked.Exchange(ref _inited, 1) == 1) { return Task.CompletedTask; }   // 重复初始化只挂一次钩子

        NetPacketHandler.ProcessPacketEvent.Register(OnPacket, HandlerPriority.Lowest);
        PlayerHooks.PlayerLogout += OnLogout;
        PlayerHooks.PlayerPostLogin += OnPostLogin;

        TShock.RestApi.Register(new SecureRestCommand("/music/status", Status, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/clients", ClientsJson, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/queue", QueueJson, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/lyrics", LyricsJson, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/search", SearchApi, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/enqueue", EnqueueApi, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/skip", Skip, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/stop", Stop, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/volume", VolumeApi, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/remove", Remove, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/clear", ClearQueue, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/config", ConfigApi, RestPermissions.restapi));
        TShock.RestApi.Register(new SecureRestCommand("/music/failures", FailuresApi, RestPermissions.restapi));

        TShockAPI.Commands.ChatCommands.Add(new Command(_commandPermission, MusicCommand, "music", "点歌"));

        _tick = new Timer(_ => Tick(), null, 1000, 1000);
        TShock.Log.Info("[RuYouMusic] 点歌机已启动（探测 " + IdProbe + " / 下发 " + IdDown + " / 上报 " + IdUp + "）");
        return Task.CompletedTask;
    }

    private static void OnPostLogin(PlayerPostLoginEventArgs e)
    {
        try
        {
            if (e.Player is null) { return; }
            // ★ 新连接必须重新握手：清掉该槽位可能残留的旧会话
            //   （否则上一局的记录/测试记录会被当成「该玩家装了插件」，面板就会显示错误的人数）
            Clients.TryRemove(e.Player.Index, out _);
            ProbeAt[e.Player.Index] = NowMs;   // ★ 记下探测时间，否则周期探测会立刻再探一次 → 客户端回两次 HELLO → 收到两次 PLAY
            Send(e.Player.Index, IdProbe, DnProbe, null, null);
        }
        catch { }
    }

    private static void OnLogout(PlayerLogoutEventArgs e)
    {
        try { if (e.Player is not null) { Clients.TryRemove(e.Player.Index, out _); } } catch { }
    }

    // ============================================================
    // 收包（C→S）
    // ============================================================

    private static void OnPacket(ref ReadonlyEventArgs<ProcessPacketEvent> args)
    {
        try
        {
            if (!_enabled) { return; }
            ProcessPacketEvent p = args.Content;
            if (p.EventType != ProcessPacketEventType.BeforeAllLogic) { return; }   // 同一包会触发两次
            ReadOnlySpan<byte> raw = p.RawData;
            if (raw.Length < 6 || raw[0] != IdUp) { return; }
            if (raw[1] != Magic[0] || raw[2] != Magic[1] || raw[3] != Magic[2] || raw[4] != Magic[3]) { return; }

            int who = p.Who;
            string name = PlayerName(who);
            byte cmd = raw[5];
            byte ver = raw[6];

            if (cmd == UpHello)
            {
                var st = new ClientState { Name = name, Version = ver, LastSeenMs = NowMs };
                new Random().NextBytes(st.Token);
                Clients[who] = st;
                Send(who, IdDown, DnSession, st.Token, w =>
                {
                    // PLAY 之前先把会话令牌给客户端
                    Array.Copy(st.Token, 0, w, 9, 8);   // frame[9] = 载荷[6]（Send 里已写过，这里保持一致）
                });
                TShock.Log.Info("[RuYouMusic] 客户端插件握手: " + name + " (#" + who + ") v" + ver + "，应答 " + Clients.Count + " 个");
                if (Current is not null) { SendPlayTo(who, st, 0); }   // 中途加入：直接同步当前歌曲与进度
                Track? nx; lock (Sync) { nx = Queue.Count > 0 ? Queue[0] : null; }
                if (nx is not null) { SendPrefetchTo(who, st, nx); }
                return;
            }

            if (!Clients.TryGetValue(who, out ClientState? c)) { return; }
            // ★ 事件上报放在令牌校验之前：它只是状态信息（谁在下载/失败/播放），伪造无意义
            if (cmd == UpEvent)
            {
                int d = 1 + PayloadOffset;                       // raw 内数据起点
                if (raw.Length < d + 9) { return; }
                byte kind = raw[d];
                long tid = BitConverter.ToInt64(raw.Slice(d + 1, 8));
                string msg = ReadStrAt(raw, d + 9);
                c.LastEventKind = kind;
                c.LastEventMs = NowMs;
                c.LastEventTrackId = tid;
                c.LastEventMsg = msg;
                TShock.Log.Info("[RuYouMusic] " + name + " → " + EventName(kind) + " (id=" + tid + ")" + (msg.Length > 0 ? " " + msg : ""));
                if (IsFailure(kind))
                {
                    string songName = Current is not null && Current.Id == tid ? Current.Name : ("id " + tid);
                    RecordFailure(who, name, songName, tid, EventName(kind), msg.Length > 0 ? msg : EventName(kind));
                }
                return;
            }

            // ★ 令牌校验：只有真正完成握手的客户端（带正确会话令牌）才能上报，防止其他玩家伪造点歌状态
            // ★ 令牌校验：控制类上报（放完/进度）必须带正确会话令牌，防止其他玩家伪造
            for (int i = 0; i < 8; i++) { if (raw[7 + i] != c.Token[i]) { return; } }
            c.LastSeenMs = NowMs;
            c.Name = name;
            if (cmd == UpFinished)
            {
                long tid = BitConverter.ToInt64(raw.Slice(1 + PayloadOffset, 8));
                if (Current is not null && Current.Id == tid && Current.DurationMs > 20000 && c.LastPosMs > 0 && c.LastPosMs < Current.DurationMs - 15000)
                {
                    // ★ 老客户端不上报事件，但会发 FINISHED 和 POSITION：播到一半就结束基本就是直链失效/下载失败
                    RecordFailure(who, name, Current.Name, Current.Id, "客户端提前结束",
                        "播到 " + (c.LastPosMs / 1000) + "s / 共 " + (Current.DurationMs / 1000) + "s（多为直链失效或下载失败）");
                }
                if (Current is not null && Current.Id == tid) { TShock.Log.Info("[RuYouMusic] " + name + " 播放结束: " + Current.Name); Next(); }
                return;
            }
            if (cmd == UpPosition)
            {
                long tid = BitConverter.ToInt64(raw.Slice(1 + PayloadOffset, 8));
                int pos = BitConverter.ToInt32(raw.Slice(1 + PayloadOffset + 8, 4));
                if (tid == (Current?.Id ?? -1) && pos >= 0)
                {
                    c.TrackId = tid;
                    c.LastPosMs = pos;
                    c.LastPosAtMs = NowMs;
                }
            }
        }
        catch { }
    }

    /// <summary>读取载荷里的 [u16 长度][UTF-8] 字符串（越界自动截断）。</summary>
    private static string ReadStrAt(ReadOnlySpan<byte> raw, int offset)
    {
        try
        {
            if (offset + 2 > raw.Length) { return ""; }
            int n = BitConverter.ToUInt16(raw.Slice(offset, 2));
            int room = Math.Max(0, raw.Length - offset - 2);
            if (n > room) { n = room; }
            return Encoding.UTF8.GetString(raw.Slice(offset + 2, n));
        }
        catch { return ""; }
    }

    private static string EventName(byte kind) => kind switch
    {
        1 => "开始下载",
        2 => "下载完成/缓存命中",
        3 => "下载失败",
        4 => "播放中",
        5 => "起播失败",
        6 => "预取完成",
        7 => "预取失败",
        _ => "事件" + kind
    };

    private static bool IsFailure(byte kind) => kind is 3 or 5 or 7;

    private static string PlayerName(int who)
    {
        try { TSPlayer? t = TShock.Players != null && who >= 0 && who < TShock.Players.Length ? TShock.Players[who] : null; return t?.Name ?? ""; }
        catch { return ""; }
    }

    // ============================================================
    // 下发（S→C）
    // ============================================================

    private static void Send(int who, byte msgId, byte cmd, byte[]? token, Action<byte[]>? fill)
    {
        try
        {
            TSPlayer? ts = TShock.Players != null && who >= 0 && who < TShock.Players.Length ? TShock.Players[who] : null;
            var c = ts?.Client;
            if (c is null || !c.IsActive || c.Socket is null) { return; }
            byte[] frame = new byte[FrameSize];
            frame[0] = (byte)(FrameSize & 0xFF);
            frame[1] = (byte)((FrameSize >> 8) & 0xFF);
            frame[2] = msgId;
            Array.Copy(Magic, 0, frame, 3, 4);
            frame[7] = cmd;
            frame[8] = Proto;
            if (token is not null) { Array.Copy(token, 0, frame, 9, Math.Min(8, token.Length)); }
            fill?.Invoke(frame);
            c.Socket.AsyncSend(frame, 0, FrameSize, (SocketSendCallback)(_ => { }), null);
        }
        catch { }
    }

    private static void SendPlayTo(int who, ClientState st, int extraOffsetMs)
    {
        Track? t = Current;
        if (t is null) { return; }
        TShock.Log.Info("[RuYouMusic] 下发 PLAY → #" + who + " " + t.Name + " id=" + t.Id + " 直链=" + (t.Url.Length == 0 ? "(空)" : t.Url.Substring(0, Math.Min(60, t.Url.Length))) + "...");
        int offset = CurrentOffsetMs + extraOffsetMs + (int)Math.Max(0, NowMs - CurrentStartedMs);
        Send(who, IdDown, DnPlay, st.Token, w =>
        {
            BitConverter.TryWriteBytes(w.AsSpan(FrameDataOffset, 8), t.Id);
            BitConverter.TryWriteBytes(w.AsSpan(FrameDataOffset + 8, 4), offset);
            BitConverter.TryWriteBytes(w.AsSpan(FrameDataOffset + 12, 4), (int)Math.Min(int.MaxValue, t.DurationMs));
            w[FrameDataOffset + 16] = (byte)Math.Clamp(_volumePercent, 0, 100);
            int p = FrameDataOffset + 24;
            p = WriteStr(w, p, t.Name);
            p = WriteStr(w, p, t.Artist);
            WriteStr(w, p, t.Url ?? "");   // v2：直链由服务端解析好，客户端直接下载（接口不稳时更可靠）
        });
    }

    /// <summary>把队列第一首（下一首）预取提示发给所有已握手客户端。</summary>
    private static void SendPrefetchToAll()
    {
        Track? nxt;
        lock (Sync) { nxt = Queue.Count > 0 ? Queue[0] : null; }
        if (nxt is null || string.IsNullOrEmpty(nxt.Url)) { return; }
        foreach (KeyValuePair<int, ClientState> kv in Clients) { SendPrefetchTo(kv.Key, kv.Value, nxt); }
    }

    private static void SendPrefetchTo(int who, ClientState st, Track t)
    {
        if (string.IsNullOrEmpty(t.Url)) { return; }
        Send(who, IdDown, DnPrefetch, st.Token, w =>
        {
            BitConverter.TryWriteBytes(w.AsSpan(FrameDataOffset, 8), t.Id);
            BitConverter.TryWriteBytes(w.AsSpan(FrameDataOffset + 8, 4), 0);
            BitConverter.TryWriteBytes(w.AsSpan(FrameDataOffset + 12, 4), (int)Math.Min(int.MaxValue, t.DurationMs));
            w[FrameDataOffset + 16] = 0;
            int p = FrameDataOffset + 24;
            p = WriteStr(w, p, t.Name);
            p = WriteStr(w, p, t.Artist);
            WriteStr(w, p, t.Url);
        });
        TShock.Log.Info("[RuYouMusic] 预取提示 → #" + who + " " + t.Name + "（客户端后台下载，切歌秒开）");
    }

    private static int WriteStr(byte[] buf, int pos, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s ?? "");
        int n = Math.Min(b.Length, ushort.MaxValue);
        BitConverter.TryWriteBytes(buf.AsSpan(pos, 2), (ushort)n);
        int room = Math.Max(0, Math.Min(n, buf.Length - pos - 2));
        if (room < n)
        {
            TShock.Log.Warning("[RuYouMusic] 载荷放不下被截断: 需要 " + n + " 字节，只剩 " + room + "（帧 " + FrameSize + "）");
        }
        Array.Copy(b, 0, buf, pos + 2, room);
        return pos + 2 + room;
    }

    private static void BroadcastPlay()
    {
        foreach (KeyValuePair<int, ClientState> kv in Clients) { SendPlayTo(kv.Key, kv.Value, 0); }
    }

    // ============================================================
    // 队列 / 播放
    // ============================================================

    private static void Play(Track t)
    {
        lock (Sync)
        {
            Current = t;
            CurrentOffsetMs = 0;
            CurrentStartedMs = NowMs;
            Lyrics.Clear();
        }
        foreach (KeyValuePair<int, ClientState> kv in Clients) { kv.Value.LastPosMs = 0; kv.Value.LastPosAtMs = 0; }
        BroadcastPlay();
        _ = Task.Run(async () => await FetchLyricsAsync(t));
        // ★ 预取：把队列下一首提前告诉客户端，让它在后台下载好 → 切歌几乎秒开
        SendPrefetchToAll();
    }

    private static void Next()
    {
        Track? nxt = null;
        lock (Sync)
        {
            if (Queue.Count > 0) { nxt = Queue[0]; Queue.RemoveAt(0); }
        }
        if (nxt is null) { StopAll(); return; }
        Play(nxt);
    }

    private static void StopAll()
    {
        lock (Sync) { Current = null; CurrentOffsetMs = 0; Lyrics.Clear(); }
        foreach (KeyValuePair<int, ClientState> kv in Clients) { Send(kv.Key, IdDown, DnStop, kv.Value.Token, null); }
    }

    private static int CurrentPositionMs()
    {
        Track? t = Current;
        if (t is null) { return 0; }
        // 优先用客户端上报的进度（更准），没有就用服务端时钟推算
        long best = -1, bestAt = 0;
        foreach (ClientState c in Clients.Values)
        {
            if (c.TrackId == t.Id && c.LastPosAtMs > bestAt) { best = c.LastPosMs; bestAt = c.LastPosAtMs; }
        }
        if (best >= 0 && NowMs - bestAt < 10000) { return (int)Math.Min(int.MaxValue, best + (NowMs - bestAt)); }
        return CurrentOffsetMs + (int)Math.Max(0, NowMs - CurrentStartedMs);
    }

    private static void Tick()
    {
        try
        {
            if (!_enabled) { return; }
            long now = NowMs;
            // 1) 探测没握手的在线玩家（每个玩家 probeSeconds 一次，避免刷屏）
            for (int i = 0; i < (TShock.Players?.Length ?? 0); i++)
            {
                TSPlayer? ts = TShock.Players![i];
                var c = ts?.Client;
                if (c is null || !c.IsActive) { continue; }
                if (Clients.ContainsKey(i)) { continue; }
                if (!ProbeAt.TryGetValue(i, out long last) || now - last > _probeSeconds * 1000L)
                {
                    ProbeAt[i] = now;
                    Send(i, IdProbe, DnProbe, null, null);
                }
            }
            // 2) 自动下一首（时长已知时）
            Track? t = Current;
            if (t is not null && _autoNext && t.DurationMs > 1500 && CurrentPositionMs() > t.DurationMs + 3000) { Next(); }
        }
        catch { }
    }

    private static readonly ConcurrentDictionary<int, long> ProbeAt = new();

    // ============================================================
    // 音乐 API
    // ============================================================

    private static async Task<JsonDocument?> GetJsonAsync(string url)
    {
        await ApiGate.WaitAsync().ConfigureAwait(false);
        try
        {
            long gap = ApiGapMs - (NowMs - _lastApiMs);
            if (gap > 0) { await Task.Delay((int)gap).ConfigureAwait(false); }
            _lastApiMs = NowMs;
            _apiCalls++;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            if (_apiHeaders.Length > 0)
            {
                foreach (string part in _apiHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0) { continue; }
                    req.Headers.TryAddWithoutValidation(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim());
                }
            }
            string body;
            using (HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false))
            {
                body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    TShock.Log.Warning("[RuYouMusic] API HTTP " + (int)resp.StatusCode + "（可能被限流）");
                    await Task.Delay(800).ConfigureAwait(false);   // 退避，别把接口打得更死
                    return null;
                }
            }
            return JsonDocument.Parse(body);
        }
        catch (Exception ex)
        {
            TShock.Log.Warning("[RuYouMusic] API 失败: " + ex.Message);
            await Task.Delay(600).ConfigureAwait(false);
            return null;
        }
        finally { ApiGate.Release(); }
    }

    private static async Task<List<Track>> SearchAsync(string kw, int count, string source = "netease")
    {
        var list = new List<Track>();
        string url = _apiBase + "?types=search&source=" + Uri.EscapeDataString(source) + "&name=" + Uri.EscapeDataString(kw) + "&count=" + Math.Clamp(count, 1, 50) + "&pages=1" + Sign(kw) + Extra();
        using JsonDocument? doc = await GetJsonAsync(url);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) { return list; }
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            string rid = Str(e, "id");
            if (rid.Length == 0) { continue; }
            string uid = Str(e, "url_id");
            var t = new Track { Name = Str(e, "name"), Album = Str(e, "album"), Source = source, RemoteId = rid, UrlId = uid.Length > 0 ? uid : rid, Id = StableId(source, rid) };
            if (e.TryGetProperty("artist", out JsonElement ar) && ar.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (JsonElement a in ar.EnumerateArray()) { names.Add(a.ToString()); }
                t.Artist = string.Join(" / ", names);
            }
            if (t.Name.Length > 0) { list.Add(t); }
        }
        return list;
    }

    /// <summary>解析直链+时长，失败自动重试（公共接口会 503/偶发空值）。</summary>
    private static async Task<bool> ResolveUrlAsync(Track t, int tries, bool fast = false)
    {
        for (int i = 0; i < Math.Max(1, tries); i++)
        {
            try
            {
                if (await FillUrlAndDurationAsync(t, fast)) { return true; }
                if (!string.IsNullOrEmpty(t.Url)) { return true; }
            }
            catch { }
            if (i + 1 < tries) { await Task.Delay(fast ? 400 : 900); }
        }
        return !string.IsNullOrEmpty(t.Url);
    }

    /// <summary>多音源解析：先用给定 id（netease）试；失败就按关键字在各音源里找第一首能出直链的。</summary>
    private static async Task<Track?> ResolvePlayableAsync(Track? byId, string kw)
    {
        if (byId is not null && await ResolveUrlAsync(byId, 3)) { return byId; }
        if (string.IsNullOrWhiteSpace(kw)) { return null; }
        foreach (string src in _sources)
        {
            List<Track> found = await SearchAsync(kw, 5, src);
            foreach (Track cand in found)
            {
                if (await ResolveUrlAsync(cand, 2)) { return cand; }
            }
        }
        return null;
    }

    private static string CacheKey(Track t) => t.Source + "|" + t.RemoteId;

    /// <summary>播放前确认直链还新鲜：队列里可能等很久，网易云直链带时间戳、joox 的 vkey 更短命，过期了客户端就会「拿不到直链」。</summary>
    private static async Task EnsureFreshUrlAsync(Track t)
    {
        if (ResolveCache.TryGetValue(CacheKey(t), out Resolved? c)
            && c.Playable && c.Url.Length > 0 && c.Url == t.Url
            && NowMs - c.AtMs < 10 * 60 * 1000)
        {
            return;   // 10 分钟内解析过，直接用
        }
        t.Url = "";
        ResolveCache.TryRemove(CacheKey(t), out _);
        if (!await ResolveUrlAsync(t, 2))
        {
            TShock.Log.Warning("[RuYouMusic] 播放前重解析仍无直链: " + t.Name);
        }
    }

    private static async Task FillUrlAndDurationAsync(Track t) => await FillUrlAndDurationAsync(t, false).ConfigureAwait(false);

    private static async Task<bool> FillUrlAndDurationAsync(Track t, bool fast)
    {
        // ★ 先查缓存：面板搜索解析过的直链，点歌时直接复用（0 次接口请求）
        string key = CacheKey(t);
        if (ResolveCache.TryGetValue(key, out Resolved? c))
        {
            long age = NowMs - c.AtMs;
            if (c.Playable && age < UrlTtlMs && c.Url.Length > 0)
            {
                Interlocked.Increment(ref _cacheHits);
                t.Url = c.Url;
                if (t.DurationMs <= 0) { t.DurationMs = c.DurationMs; }
                return true;
            }
            if (!c.Playable && age < NegTtlMs) { return false; }
        }
        // ★ 用 url_id（部分歌与 id 不同）；并依次尝试 320/128 码率——某些歌只有低码率可取
        string rid = t.UrlId.Length > 0 ? t.UrlId : t.RemoteId;
        foreach (int br in (fast ? new[] { 320 } : new[] { 320, 128 }))
        {
            string url = _apiBase + "?types=url&source=" + Uri.EscapeDataString(t.Source) + "&id=" + Uri.EscapeDataString(rid) + "&br=" + br + Sign(Uri.EscapeDataString(rid)) + Extra();
            using JsonDocument? doc = await GetJsonAsync(url);
            if (doc is null) { continue; }
            JsonElement r = doc.RootElement;
            string u = Str(r, "url");
            if (u.Length == 0) { continue; }
            t.Url = u;
            long size = Long(r, "size");
            long brr = Long(r, "br");
            if (size > 0 && brr > 0) { t.DurationMs = (long)(size / (double)(brr * 125) * 1000.0); }
            ResolveCache[key] = new Resolved { Url = u, DurationMs = t.DurationMs, AtMs = NowMs, Playable = true };
            return true;
        }
        // ★ 兜底：autosource —— 按 歌名/歌手 让服务端换个能播的音源（网页能放 VIP 歌就是靠它；
        //   公共 API 这个接口是空的，只有自建网关的浏览器模式（带站点自己的会话）才拿得到）
        if (!fast && t.Name.Length > 0)
        {
            string payload = "{\"name\":\"" + JsonEsc(t.Name) + "\",\"artist\":\"" + JsonEsc(t.Artist) + "\",\"album\":\"" + JsonEsc(t.Album) + "\",\"isrc\":\"\",\"duration\":" + (t.DurationMs > 0 ? t.DurationMs : 0) + "}";
            string autoUrl = _apiBase + "?types=autosource&source=" + Uri.EscapeDataString(t.Source) + "&br=320&name=" + Uri.EscapeDataString(payload) + Sign(payload) + Extra();
            string u2 = "";
            JsonElement r2 = default;
            for (int attempt = 0; attempt < 2 && u2.Length == 0; attempt++)   // ★ 站点 autosource 是间歇性的，空结果就再试一次
            {
                if (attempt > 0) { await Task.Delay(1500).ConfigureAwait(false); }
                using JsonDocument? doc2 = await GetJsonAsync(autoUrl);
                if (doc2 is null) { continue; }
                r2 = doc2.RootElement;
                u2 = Str(r2, "url");
            }
            if (true)
            {
                if (u2.Length > 0)
                {
                    t.Url = u2;
                    string src2 = Str(r2, "source");
                    string nm2 = Str(r2, "name");
                    if (src2.Length > 0) { t.Source = src2; }
                    if (nm2.Length > 0 && t.Name.Length == 0) { t.Name = nm2; }
                    long size2 = Long(r2, "size");
                    long brr2 = Long(r2, "br");
                    if (size2 > 0 && brr2 > 0) { t.DurationMs = (long)(size2 / (double)(brr2 * 125) * 1000.0); }
                    ResolveCache[key] = new Resolved { Url = u2, DurationMs = t.DurationMs, AtMs = NowMs, Playable = true };
                    TShock.Log.Info("[RuYouMusic] autosource 兜底成功: " + t.Name + " → " + (src2.Length > 0 ? src2 : "?"));
                    return true;
                }
            }
        }
        ResolveCache[key] = new Resolved { Playable = false, AtMs = NowMs };
        return false;
    }

    /// <summary>极简 JSON 字符串转义。</summary>
    private static string JsonEsc(string v)
    {
        if (string.IsNullOrEmpty(v)) { return ""; }
        var sb = new StringBuilder(v.Length + 8);
        foreach (char ch in v)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: if (ch < 0x20) { sb.Append("\\u").Append(((int)ch).ToString("x4")); } else { sb.Append(ch); } break;
            }
        }
        return sb.ToString();
    }

    private static async Task FetchLyricsAsync(Track t)
    {
        try
        {
            string url = _apiBase + "?types=lyric&source=" + Uri.EscapeDataString(t.Source) + "&id=" + Uri.EscapeDataString(t.RemoteId) + Sign(Uri.EscapeDataString(t.RemoteId)) + Extra();
            using JsonDocument? doc = await GetJsonAsync(url);
            if (doc is null) { return; }
            string lrc = Str(doc.RootElement, "lyric");
            string tlrc = Str(doc.RootElement, "tlyric");
            List<LyricLine> lines = ParseLrc(lrc, tlrc);
            lock (Sync)
            {
                if (Current is null || Current.Id != t.Id) { return; }   // 已经换歌了
                Lyrics.Clear();
                Lyrics.AddRange(lines);
            }
        }
        catch { }
    }

    private static readonly Regex LrcTime = new Regex(@"\[(\d+):(\d+)[.:](\d+)\]", RegexOptions.Compiled);

    private static List<LyricLine> ParseLrc(string lrc, string tlrc)
    {
        var map = new Dictionary<int, string>();
        void Parse(string text, bool translation)
        {
            if (string.IsNullOrEmpty(text)) { return; }
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match m = LrcTime.Match(line);
                if (!m.Success) { continue; }
                double min = double.Parse(m.Groups[1].Value);
                double sec = double.Parse(m.Groups[2].Value);
                string frac = m.Groups[3].Value;
                double ms = double.Parse(frac) / Math.Pow(10, frac.Length);
                int key = (int)Math.Round((min * 60 + sec + ms) * 1000);
                string body = LrcTime.Replace(line, "").Trim();
                if (body.Length == 0) { continue; }
                map[key] = map.TryGetValue(key, out string? old) && translation ? old + "  /  " + body : (translation && old is not null ? old + "  /  " + body : body);
            }
        }
        Parse(lrc, false);
        Parse(tlrc, true);
        return map.OrderBy(kv => kv.Key).Select(kv => new LyricLine { Ms = kv.Key, Text = kv.Value }).ToList();
    }

    private static string Str(JsonElement e, string key)
    {
        try { return e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : ""; } catch { return ""; }
    }

    private static long Long(JsonElement e, string key)
    {
        try
        {
            if (!e.TryGetProperty(key, out JsonElement v)) { return 0; }
            if (v.ValueKind == JsonValueKind.Number) { return v.GetInt64(); }
            return long.TryParse(v.ToString(), out long n) ? n : 0;
        }
        catch { return 0; }
    }

    // ============================================================
    // 入队
    // ============================================================

    private static async Task<string> EnqueueAsync(string? idOrQuery, string by, bool byQuery, string hint = "")
    {
        Track? t;
        if (!byQuery && long.TryParse(idOrQuery, out long id))
        {
            string rid = id.ToString(CultureInfo.InvariantCulture);
            var byId = new Track { Id = StableId("netease", rid), Source = "netease", RemoteId = rid, Name = hint.Length > 0 ? hint : ("ID " + id), Artist = "" };
            t = await ResolvePlayableAsync(byId, hint);
        }
        else
        {
            t = await ResolvePlayableAsync(null, idOrQuery ?? "");
            if (t is null)
            {
                List<Track> any = await SearchAsync(idOrQuery ?? "", 1, _sources[0]);
                if (any.Count == 0) { return "没有搜到：" + idOrQuery; }
            }
        }
        if (t is null)
        {
            RecordFailure(-1, by, hint.Length > 0 ? hint : (idOrQuery ?? ""), -1, "解析直链", "接口限流/版权受限，取不到直链");
            return "取不到可播放的直链（接口限流或版权受限），换一首歌或稍后重试";
        }
        int depth;
        lock (Sync)
        {
            if (Queue.Count >= _maxQueue) { return "队列已满（上限 " + _maxQueue + "）"; }
            t.By = by;
            if (Current is null) { depth = 0; }
            else { Queue.Add(t); depth = Queue.Count; }
        }
        if (depth == 0) { await EnsureFreshUrlAsync(t); Play(t); return "开始播放：" + t.Name + " - " + t.Artist; }
        if (depth == 1) { SendPrefetchToAll(); }   // 刚好成为下一首 → 立刻让客户端预下载
        return "已加入队列第 " + depth + " 位：" + t.Name + " - " + t.Artist;
    }

    // ============================================================
    // REST
    // ============================================================

    private static string GetParam(RestRequestArgs args, string key)
    {
        try { return args.Parameters[key] ?? string.Empty; } catch { return string.Empty; }
    }

    private static int GetInt(RestRequestArgs args, string key, int fallback)
    {
        return int.TryParse(GetParam(args, key), out int v) ? v : fallback;
    }

    private static bool IsOn(string s) => s is "1" or "true" or "on" or "yes";

    private static object Status(RestRequestArgs args)
    {
        Track? t = Current;
        return new
        {
            status = "200",
            enabled = _enabled,
            playing = t is not null,
            track = t is null ? null : new { id = t.Id, name = t.Name, artist = t.Artist, durationMs = t.DurationMs, by = t.By, url = t.Url },
            positionMs = CurrentPositionMs(),
            volumePercent = _volumePercent,
            queueCount = Queue.Count,
            clientCount = Clients.Count,
            failures = Clients.Where(kv => IsFailure(kv.Value.LastEventKind))
                              .Select(kv => new { who = kv.Key, name = kv.Value.Name, reason = kv.Value.LastEventMsg.Length > 0 ? kv.Value.LastEventMsg : EventName(kv.Value.LastEventKind), agoMs = Math.Max(0, NowMs - kv.Value.LastEventMs) })
                              .ToList(),
            lyricCount = Lyrics.Count,
            autoNext = _autoNext
        };
    }

    private static object ClientsJson(RestRequestArgs args)
    {
        long now = NowMs;
        var list = new List<object>();
        for (int i = 0; i < (TShock.Players?.Length ?? 0); i++)
        {
            TSPlayer? ts = TShock.Players![i];
            var c = ts?.Client;
            if (c is null || !c.IsActive) { continue; }
            bool has = Clients.TryGetValue(i, out ClientState? st);
            list.Add(new
            {
                who = i,
                name = ts!.Name ?? "",
                hasPlugin = has,
                version = has ? st!.Version : 0,
                lastSeenMs = has ? Math.Max(0, now - st!.LastSeenMs) : -1,
                positionMs = has ? st!.LastPosMs : -1,
                lastEvent = has ? EventName(st!.LastEventKind) : "",
                lastEventFailed = has && IsFailure(st!.LastEventKind),
                lastEventMs = has && st!.LastEventMs > 0 ? Math.Max(0, now - st.LastEventMs) : -1,
                lastEventTrackId = has ? st!.LastEventTrackId : -1,
                lastMessage = has ? st!.LastEventMsg : ""
            });
        }
        return new { status = "200", total = list.Count, capable = Clients.Count, clients = list };
    }

    private static object QueueJson(RestRequestArgs args)
    {
        lock (Sync)
        {
            return new
            {
                status = "200",
                current = Current is null ? null : new { id = Current.Id, name = Current.Name, artist = Current.Artist, durationMs = Current.DurationMs, by = Current.By },
                queue = Queue.Select((t, i) => new { index = i, id = t.Id, name = t.Name, artist = t.Artist, durationMs = t.DurationMs, by = t.By }).ToList()
            };
        }
    }

    private static object LyricsJson(RestRequestArgs args)
    {
        lock (Sync)
        {
            return new
            {
                status = "200",
                id = Current?.Id ?? -1,
                name = Current?.Name ?? "",
                artist = Current?.Artist ?? "",
                positionMs = CurrentPositionMs(),
                durationMs = Current?.DurationMs ?? 0,
                lines = Lyrics.Select(l => new { ms = (long)l.Ms, text = l.Text }).ToList()
            };
        }
    }

    private static object SearchApi(RestRequestArgs args)
    {
        string q = GetParam(args, "q");
        if (q.Length == 0) { q = GetParam(args, "name"); }
        int count = GetInt(args, "count", _searchCount);
        bool check = IsOn(GetParam(args, "check"));   // check=1：逐个解析直链，标注能否播放（会慢一点）
        List<Track> found = SearchAsync(q, count).GetAwaiter().GetResult();
        var list = new List<object>();
        foreach (Track t in found)
        {
            bool playable = false;
            if (check) { playable = ResolveUrlAsync(t, 1, true).GetAwaiter().GetResult(); }
            list.Add(new { id = t.Id, name = t.Name, artist = t.Artist, source = t.Source, playable = check ? playable : true });
        }
        return new { status = "200", query = q, didCheck = check, results = list };
    }

    private static object EnqueueApi(RestRequestArgs args)
    {
        string by = GetParam(args, "by");
        if (by.Length == 0) { by = "REST"; }
        string id = GetParam(args, "id");
        string q = GetParam(args, "q");
        string hint = GetParam(args, "name");    // 面板传来的「歌名 歌手」，用于跨音源兜底搜索
        if (hint.Length == 0) { hint = q; }
        bool byQuery = id.Length == 0;
        string msg = EnqueueAsync(byQuery ? q : id, by, byQuery, hint).GetAwaiter().GetResult();
        return new { status = "200", message = msg, queue = QueueJson(args) };
    }

    private static object Skip(RestRequestArgs args) { Next(); return new { status = "200", message = "已切歌", queue = QueueJson(args) }; }

    private static object Stop(RestRequestArgs args) { StopAll(); return new { status = "200", message = "已停止" }; }

    private static object VolumeApi(RestRequestArgs args)
    {
        int pct = GetInt(args, "pct", -1);
        if (pct >= 0)
        {
            _volumePercent = Math.Clamp(pct, 0, 100);
            foreach (KeyValuePair<int, ClientState> kv in Clients) { Send(kv.Key, IdDown, DnVolume, kv.Value.Token, w => w[FrameDataOffset] = (byte)_volumePercent); }
            SaveSettings();
        }
        return new { status = "200", volumePercent = _volumePercent };
    }

    private static object Remove(RestRequestArgs args)
    {
        int idx = GetInt(args, "index", -1);
        lock (Sync)
        {
            if (idx >= 0 && idx < Queue.Count) { Queue.RemoveAt(idx); }
        }
        return new { status = "200", queue = QueueJson(args) };
    }

    private static object ClearQueue(RestRequestArgs args)
    {
        lock (Sync) { Queue.Clear(); }
        return new { status = "200", message = "队列已清空" };
    }

    private static object FailuresApi(RestRequestArgs args)
    {
        if (IsOn(GetParam(args, "clearCache"))) { ResolveCache.Clear(); return new { status = "200", message = "已清空直链缓存" }; }
        if (IsOn(GetParam(args, "clear"))) { lock (Sync) { Failures.Clear(); } return new { status = "200", message = "已清空失败记录" }; }
        int limit = Math.Clamp(GetInt(args, "limit", 20), 1, 60);
        long now = NowMs;
        lock (Sync)
        {
            return new
            {
                status = "200",
                count = Failures.Count,
                failures = Failures.Skip(Math.Max(0, Failures.Count - limit)).Reverse()
                    .Select(f => new { agoMs = Math.Max(0, now - f.Ms), who = f.Who, player = f.Player, song = f.Song, trackId = f.TrackId, stage = f.Stage, reason = f.Reason })
                    .ToList()
            };
        }
    }

    private static object ConfigApi(RestRequestArgs args)
    {
        bool touched = GetParam(args, "on").Length > 0 || GetParam(args, "off").Length > 0 || GetParam(args, "autoNext").Length > 0
                       || GetParam(args, "maxQueue").Length > 0 || GetParam(args, "searchCount").Length > 0 || GetParam(args, "probeSeconds").Length > 0
                       || GetParam(args, "apiBase").Length > 0 || GetParam(args, "apiSign").Length > 0;
        if (touched)
        {
            if (IsOn(GetParam(args, "on"))) { _enabled = true; }
            if (IsOn(GetParam(args, "off"))) { _enabled = false; }
            if (GetParam(args, "autoNext").Length > 0) { _autoNext = IsOn(GetParam(args, "autoNext")); }
            _maxQueue = Math.Clamp(GetInt(args, "maxQueue", _maxQueue), 1, 200);
            _searchCount = Math.Clamp(GetInt(args, "searchCount", _searchCount), 1, 50);
            _probeSeconds = Math.Clamp(GetInt(args, "probeSeconds", _probeSeconds), 3, 600);
            if (GetParam(args, "apiBase").Length > 0) { _apiBase = GetParam(args, "apiBase"); }
            try { if (args.Parameters["apiExtra"] is not null) { _apiExtra = GetParam(args, "apiExtra"); } } catch { }
            try { if (args.Parameters["apiHeaders"] is not null) { _apiHeaders = GetParam(args, "apiHeaders"); } } catch { }
            if (GetParam(args, "apiSign").Length > 0) { _apiSign = GetInt(args, "apiSign", _apiSign); }
            SaveSettings();
        }
        return new
        {
            status = "200",
            enabled = _enabled,
            autoNext = _autoNext,
            maxQueue = _maxQueue,
            searchCount = _searchCount,
            probeSeconds = _probeSeconds,
            volumePercent = _volumePercent,
            configPath = _configPath ?? "",
            api = _apiBase,
            apiCalls = _apiCalls,
            cacheHits = _cacheHits,
            cacheSize = ResolveCache.Count,
            apiSign = _apiSign,
            apiExtra = _apiExtra,
            apiHeaders = _apiHeaders.Length > 0 ? "(已设置)" : "",
            sources = _sources
        };
    }

    // ============================================================
    // 指令
    // ============================================================

    private static void MusicCommand(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "help";
        try
        {
            switch (sub)
            {
                case "search":
                case "s":
                {
                    string kw = string.Join(" ", args.Parameters.Skip(1));
                    if (kw.Length == 0) { args.Player.SendInfoMessage("用法: /music search 关键字"); return; }
                    List<Track> found = SearchAsync(kw, _searchCount).GetAwaiter().GetResult();
                    if (found.Count == 0) { args.Player.SendInfoMessage("没有搜到：" + kw); return; }
                    args.Player.SendInfoMessage("搜索到 " + found.Count + " 首：");
                    for (int i = 0; i < found.Count; i++) { args.Player.SendInfoMessage("  " + (i + 1) + ". " + found[i].Name + " - " + found[i].Artist + "  (id " + found[i].Id + ")"); }
                    args.Player.SendInfoMessage("点歌: /music add " + found[0].Id + "   或  /music add " + kw);
                    break;
                }
                case "add":
                case "点歌":
                {
                    string kw = string.Join(" ", args.Parameters.Skip(1));
                    if (kw.Length == 0) { args.Player.SendInfoMessage("用法: /music add 关键字 或 /music add 歌曲id"); return; }
                    bool byQuery = !long.TryParse(kw, out _);
                    string msg = EnqueueAsync(kw, args.Player.Name, byQuery).GetAwaiter().GetResult();
                    args.Player.SendInfoMessage(msg);
                    foreach (TSPlayer pl in TShock.Players ?? Array.Empty<TSPlayer>())
                    {
                        try { pl?.SendInfoMessage(args.Player.Name + " 点了首歌：" + msg); } catch { }
                    }
                    break;
                }
                case "list":
                {
                    lock (Sync)
                    {
                        if (Current is null) { args.Player.SendInfoMessage("当前没有播放。队列 " + Queue.Count + " 首。"); }
                        else { args.Player.SendInfoMessage("正在播放：" + Current.Name + " - " + Current.Artist + "（" + (CurrentPositionMs() / 1000) + "s / " + (Current.DurationMs / 1000) + "s）"); }
                        for (int i = 0; i < Queue.Count; i++) { args.Player.SendInfoMessage("  " + (i + 1) + ". " + Queue[i].Name + " - " + Queue[i].Artist); }
                    }
                    break;
                }
                case "skip":
                case "next":
                    Next();
                    args.Player.SendInfoMessage("已切歌");
                    break;
                case "stop":
                    StopAll();
                    args.Player.SendInfoMessage("已停止播放");
                    break;
                case "vol":
                {
                    int pct = args.Parameters.Count > 1 && int.TryParse(args.Parameters[1], out int v) ? Math.Clamp(v, 0, 100) : -1;
                    if (pct < 0) { args.Player.SendInfoMessage("当前音量 " + _volumePercent + "，用法 /music vol 0-100"); return; }
                    _volumePercent = pct;
                    foreach (KeyValuePair<int, ClientState> kv in Clients) { Send(kv.Key, IdDown, DnVolume, kv.Value.Token, w => w[FrameDataOffset] = (byte)pct); }
                    SaveSettings();
                    args.Player.SendInfoMessage("音量已设为 " + pct);
                    break;
                }
                case "clients":
                {
                    string[] names = Clients.Values.Select(c => c.Name).Where(n => n.Length > 0).ToArray();
                    args.Player.SendInfoMessage("装了客户端插件的玩家 " + names.Length + " 人：" + (names.Length == 0 ? "（无）" : string.Join(", ", names)));
                    break;
                }
                default:
                    args.Player.SendInfoMessage("/music search|add|list|skip|stop|vol|clients");
                    break;
            }
        }
        catch (Exception ex) { args.Player.SendErrorMessage("点歌机出错: " + ex.Message); }
    }

    // ============================================================
    // 配置
    // ============================================================

    private static void LoadSettings()
    {
        try
        {
            if (_configPath is null || !File.Exists(_configPath)) { return; }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_configPath));
            JsonElement r = doc.RootElement;
            _enabled = !r.TryGetProperty("enabled", out JsonElement en) || en.GetBoolean();
            _maxQueue = r.TryGetProperty("maxQueue", out JsonElement mq) ? mq.GetInt32() : _maxQueue;
            _searchCount = r.TryGetProperty("searchCount", out JsonElement sc) ? sc.GetInt32() : _searchCount;
            _probeSeconds = r.TryGetProperty("probeSeconds", out JsonElement ps) ? ps.GetInt32() : _probeSeconds;
            _autoNext = !r.TryGetProperty("autoNext", out JsonElement an) || an.GetBoolean();
            _volumePercent = r.TryGetProperty("volumePercent", out JsonElement vp) ? vp.GetInt32() : _volumePercent;
            _commandPermission = r.TryGetProperty("commandPermission", out JsonElement cp) ? (cp.GetString() ?? _commandPermission) : _commandPermission;
            _apiBase = r.TryGetProperty("apiBase", out JsonElement ab) ? (ab.GetString() ?? _apiBase) : _apiBase;
            _apiExtra = r.TryGetProperty("apiExtra", out JsonElement ae) ? (ae.GetString() ?? _apiExtra) : _apiExtra;
            _apiHeaders = r.TryGetProperty("apiHeaders", out JsonElement ah) ? (ah.GetString() ?? _apiHeaders) : _apiHeaders;
            _apiSign = r.TryGetProperty("apiSign", out JsonElement asg) && asg.TryGetInt32(out int sv) ? sv : _apiSign;
            if (r.TryGetProperty("sources", out JsonElement sc2) && sc2.ValueKind == JsonValueKind.Array)
            {
                var arr = new List<string>();
                foreach (JsonElement e2 in sc2.EnumerateArray()) { string v2 = e2.ToString(); if (v2.Length > 0) { arr.Add(v2); } }
                if (arr.Count > 0) { _sources = arr.ToArray(); }
            }
        }
        catch (Exception ex) { TShock.Log.Warning("[RuYouMusic] 读取配置失败: " + ex.Message); }
    }

    private static void SaveSettings()
    {
        try
        {
            if (_configPath is null) { return; }
            var o = new Dictionary<string, object>
            {
                ["enabled"] = _enabled,
                ["maxQueue"] = _maxQueue,
                ["searchCount"] = _searchCount,
                ["probeSeconds"] = _probeSeconds,
                ["autoNext"] = _autoNext,
                ["volumePercent"] = _volumePercent,
                ["sources"] = _sources,
            ["apiBase"] = _apiBase,
            ["apiExtra"] = _apiExtra,
            ["apiHeaders"] = _apiHeaders,
            ["apiSign"] = _apiSign,
            ["commandPermission"] = _commandPermission
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
