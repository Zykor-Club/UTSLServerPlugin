using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using R.PluginSwitch.Hooks;
using Terraria;
using Terraria.Net.Sockets;

namespace R.RuYouMusicClient;

/// <summary>
/// RuYouMusicClient v1.0.0 —— 客户端侧：跟随服务端点歌机播放网络音乐。
///
///   收 129（探测）→ 回 HELLO
///   收 67（命令）：SESSION 存令牌 / PLAY 下载并用 MCI 播放 / STOP / VOLUME
///   每 ~2 秒回 POSITION；MCI 由 playing 变 stopped 时回 FINISHED（服务端据此自动下一首）
///
///   帧：[长度:2 LE][类型:1][载荷:256]
///   载荷：[0..3]=RYM1 [4]=cmd [5]=版本 [6..13]=令牌 [14..15]=保留 [16..]=数据
///   （收包侧 RawData 不含长度头：RawData[0]=类型，RawData[1+i]=载荷[i]）
/// </summary>
[Plugin(20261012, "RuYouMusicClient", "星梦", "跟随服务端点歌机播放网络音乐", 1)]
public class ClientMain
{
    internal const byte IdProbe = 129;
    internal const byte IdDown = 67;
    internal const byte IdUp = 15;
    internal const int FrameSize = 260;
    internal const int PayloadHead = 16;

    internal const byte UpHello = 1, UpFinished = 3, UpPosition = 4, UpEvent = 5;
    internal const byte DnProbe = 1, DnPlay = 2, DnStop = 3, DnVolume = 4, DnSession = 5, DnPrefetch = 6;

    internal static readonly byte[] Magic = { 0x52, 0x59, 0x4D, 0x31 };
    internal static readonly byte[] Token = new byte[8];
    internal static volatile bool Authed;

    static ClientMain()
    {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }   // .NET 4.x 默认可能不含 TLS1.2
        Log("客户端插件载入（协议 v1）");
        GameHooks.NetGetData.Register(OnNetGetData);
        GameHooks.PlayerUpdate.Register(OnPlayerUpdate);
        GameHooks.PreUpdate.Register(OnPreUpdate);
    }

    // ===== 收包 =====
    private static void OnNetGetData(HGetData e)
    {
        try
        {
            if (e.MSGType == IdProbe)
            {
                Authed = false;
                SendUp(UpHello, null);
                e.Handled = true;
                return;
            }
            if (e.MSGType != IdDown) { return; }

            BinaryReader r = NetMessage.buffer[256].reader;
            r.BaseStream.Position = e.Start + 1;
            byte[] head = r.ReadBytes(PayloadHead);
            if (head.Length < PayloadHead) { e.Handled = true; return; }
            for (int i = 0; i < 4; i++) { if (head[i] != Magic[i]) { return; } }   // 不是本插件的包 → 交还原版

            byte cmd = head[4];
            Array.Copy(head, 6, Token, 0, 8);
            switch (cmd)
            {
                case DnSession:
                    Authed = true;
                    Log("收到 SESSION，令牌=" + BitConverter.ToString(Token).Replace("-", ""));
                    Notify("点歌机已连接");
                    break;
                case DnPlay:
                {
                    long trackId = r.ReadInt64();
                    int offsetMs = r.ReadInt32();
                    r.ReadInt32();                       // durationMs：客户端暂不用
                    int vol = r.ReadByte();
                    r.ReadBytes(7);                      // 对齐到数据 +24
                    string name = ReadStr(r);
                    string artist = ReadStr(r);
                    string url = "";
                    try { url = ReadStr(r); } catch { url = ""; }   // v2：服务端解析好的直链
                    // ★ 直链校验：被截断的 URL 请求出去只会得到 400/403，宁可当没有（走自解析）
                    if (url.Length > 0 && (url.IndexOf("://") < 0 || url.Length < 25))
                    {
                        Log("服务端直链疑似被截断（长度 " + url.Length + "），丢弃: " + url);
                        url = "";
                    }
                    Log("收到 PLAY: id=" + trackId + " 偏移=" + offsetMs + "ms 音量=" + vol + " 歌名=" + name + " 歌手=" + artist
                        + " 直链=" + (url.Length == 0 ? "(空)" : url.Substring(0, Math.Min(70, url.Length)) + "..."));
                    Music.SetVolume(vol);
                    Music.Play(trackId, name, artist, offsetMs, url);
                    break;
                }
                case DnPrefetch:
                {
                    long pfId = r.ReadInt64();
                    r.ReadInt32();                       // 偏移（预取不用）
                    r.ReadInt32();                       // 时长
                    r.ReadByte();                        // 音量占位
                    r.ReadBytes(7);
                    string pfName = ReadStr(r);
                    string pfArtist = ReadStr(r);
                    string pfUrl = ReadStr(r);
                    Log("收到 PREFETCH: id=" + pfId + " " + pfName + " - " + pfArtist + " 直链=" + (pfUrl.Length == 0 ? "(空)" : pfUrl.Substring(0, Math.Min(50, pfUrl.Length)) + "..."));
                    Music.Prefetch(pfId, pfName, pfArtist, pfUrl);
                    break;
                }
                case DnStop:
                    Log("收到 STOP");
                    Music.Stop();
                    break;
                case DnVolume:
                    Music.SetVolume(r.ReadByte());
                    break;
            }
            e.Handled = true;
        }
        catch (Exception ex) { Log("收包异常: " + ex); Notify("收包异常: " + ex.Message); }
    }

    private static void OnPlayerUpdate() { try { Music.Update(); } catch { } }

    private static long _menuSinceMs;   // 菜单态起始时间（0=不在菜单）

    private static void OnPreUpdate(HandledEventArgs e)
    {
        try
        {
            if (!Main.gameMenu) { _menuSinceMs = 0; return; }
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if (_menuSinceMs == 0) { _menuSinceMs = now; return; }
            // ★ 刚进服/切世界时 gameMenu 会短暂为真：必须等它连续 3 秒才认定"真的回到主菜单"，
            //   否则会把刚收到的 PLAY 立刻掐掉（实测"刚进服最容易没声音"就是这个）
            if (now - _menuSinceMs < 3000) { return; }
            if (Authed || Music.IsPlaying) { Authed = false; Music.StopAll(); }
        }
        catch { }
    }

    // ===== 发包 =====
    internal static void SendUp(byte cmd, Action<BinaryWriter> fill)
    {
        try
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((ushort)FrameSize);
            w.Write(IdUp);
            w.Write(Magic);
            w.Write(cmd);
            w.Write((byte)1);
            w.Write(Token);
            w.Write((ushort)0);
            if (fill != null) { fill(w); }
            if (ms.Length < FrameSize) { w.Write(new byte[FrameSize - ms.Length]); }
            byte[] frame = ms.ToArray();
            ISocket sock = Netplay.Connection != null ? Netplay.Connection.Socket : null;
            if (sock != null) { sock.AsyncSend(frame, 0, frame.Length, OnSent, null); }
        }
        catch { }
    }

    private static void OnSent(object state) { }

    internal static void WriteStr(BinaryWriter w, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s ?? "");
        int n = Math.Min(b.Length, 200);
        w.Write((ushort)n);
        w.Write(b, 0, n);
    }

    /// <summary>把播放过程中的关键状态/失败原因回报服务端（面板直接能看到"谁没出声、为什么"）。</summary>
    internal static void ReportEvent(byte kind, long trackId, string msg)
    {
        try
        {
            SendUp(UpEvent, delegate(BinaryWriter w) { w.Write(kind); w.Write(trackId); WriteStr(w, msg ?? ""); });
        }
        catch { }
    }

    internal static string ReadStr(BinaryReader r)
    {
        int n = r.ReadUInt16();
        byte[] b = r.ReadBytes(n);
        return Encoding.UTF8.GetString(b);
    }

    /// <summary>诊断日志：写 <游戏目录>\MusicCache\_client.log（方便远程排查）。</summary>
    internal static void Log(string msg)
    {
        try
        {
            string dir = Path.Combine(Path.GetDirectoryName(typeof(Main).Assembly.Location), "MusicCache");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            File.AppendAllText(Path.Combine(dir, "_client.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    internal static void Notify(string msg)
    {
        try { Main.NewText("[点歌机] " + msg, 195, 177, 225); } catch { }
    }
}

internal static class Music
{
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string command, StringBuilder buffer, int bufferSize, IntPtr hwndCallback);

    private const string Api = "https://music-api.gdstudio.xyz/api.php";
    private static readonly object Sync = new();

    private static string _cacheDir;
    private static string _file;
    private static long _trackId = -1;
    private static string _name = "";
    private static string _artist = "";
    private static int _offsetMs;
    private static int _volume = 80;
    private static bool _playing;
    private static bool _wasPlaying;
    private static bool _pendingPlay;
    private static bool _pendingStop;
    private static long _startedAtMs;
    private static long _lastReportMs;
    private static string _lastModeLogged = "";
    private static string _url = "";
    private static bool _downloading;

    private static long NowMs { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; } }

    internal static bool IsPlaying { get { lock (Sync) { return _playing; } } }

    internal static string CacheDir()
    {
        if (_cacheDir == null)
        {
            string baseDir = Path.GetDirectoryName(typeof(Main).Assembly.Location);
            _cacheDir = Path.Combine(baseDir, "MusicCache");
            if (!Directory.Exists(_cacheDir)) { Directory.CreateDirectory(_cacheDir); }
        }
        return _cacheDir;
    }

    private static int _prefetching;
    private static long _pfId = -1;
    private static string _pfName = "", _pfArtist = "", _pfUrl = "";

    /// <summary>记账预取：先记下来，等当前曲的下载/起播忙完再跑，避免和正在播的歌抢带宽。</summary>
    internal static void Prefetch(long trackId, string name, string artist, string url)
    {
        if (trackId <= 0) { return; }
        string path = Path.Combine(CacheDir(), trackId + ".mp3");
        try { if (File.Exists(path) && new FileInfo(path).Length > 1024) { ClientMain.Log("预取跳过（已有缓存）: " + name); return; } } catch { }
        lock (Sync)
        {
            if (_trackId == trackId && (_playing || _downloading)) { return; }
            _pfId = trackId; _pfName = name; _pfArtist = artist; _pfUrl = url;
        }
        TryStartPrefetch();
    }

    /// <summary>条件满足才真正开始预取：一次一首，且当前没有正在进行的下载。</summary>
    private static void TryStartPrefetch()
    {
        long id; string name, artist, url;
        lock (Sync)
        {
            if (_pfId <= 0 || _downloading) { return; }
            id = _pfId; name = _pfName; artist = _pfArtist; url = _pfUrl;
            _pfId = -1;
        }
        if (Interlocked.Exchange(ref _prefetching, 1) == 1)
        {
            lock (Sync) { if (_pfId <= 0) { _pfId = id; _pfName = name; _pfArtist = artist; _pfUrl = url; } }
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                string path = Path.Combine(CacheDir(), id + ".mp3");
                if (File.Exists(path) && new FileInfo(path).Length > 1024) { ClientMain.Log("预取跳过（已有缓存）: " + name); return; }
                string use = url;
                if (string.IsNullOrEmpty(use)) { ClientMain.Log("预取: 服务端没给直链，自己解析 " + name); use = FetchUrl(id); }
                if (string.IsNullOrEmpty(use)) { ClientMain.Log("预取失败（无直链）: " + name); return; }
                long n = DownloadToFile(use, path);
                ClientMain.Log("预取完成: " + name + " " + n + " B（切歌时将秒开）");
                ClientMain.ReportEvent(6, id, "预取完成 " + (n / 1024) + " KB");
            }
            catch (Exception ex)
            {
                ClientMain.Log("预取失败: " + name + " " + ex.Message);
                ClientMain.ReportEvent(7, id, "预取失败: " + ex.Message);
                try { string path = Path.Combine(CacheDir(), id + ".mp3"); if (File.Exists(path)) { File.Delete(path); } } catch { }
            }
            finally { Interlocked.Exchange(ref _prefetching, 0); TryStartPrefetch(); }
        });
    }

    internal static void Play(long trackId, string name, string artist, int offsetMs, string url)
    {
        // ★ 去重：同一首歌已经在下/已在播就不再重来。服务端「握手 PLAY」与「广播 PLAY」可能各发一次，
        //   重复触发会二次下载同一个文件、互相覆盖与删除，导致播到几秒后中断（实测就是这个现象）。
        lock (Sync)
        {
            if (_trackId == trackId && (_playing || _downloading || _pendingPlay))
            {
                ClientMain.Log("忽略重复的 PLAY（同一首已在处理: id=" + trackId + " 在播=" + _playing + " 下载中=" + _downloading + "）");
                return;
            }
        }
        StopInternal(false);
        lock (Sync)
        {
            _trackId = trackId;
            _name = name == null ? "" : name;
            _artist = artist == null ? "" : artist;
            _offsetMs = Math.Max(0, offsetMs);
            _startedAtMs = NowMs;
            _url = url == null ? "" : url;
        }
        string path = Path.Combine(CacheDir(), trackId + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 1024)
        {
            ClientMain.Log("Play: id=" + trackId + " 命中缓存 " + path);
            ClientMain.ReportEvent(2, trackId, "缓存命中");
            StartPlayback(path);
            ClientMain.Notify("开始播放: " + _name + " - " + _artist);
            return;
        }
        ClientMain.Log("Play: id=" + trackId + " 缓存未命中，准备下载（服务端直链=" + (_url.Length == 0 ? "无" : "有") + "）");
        ClientMain.ReportEvent(1, trackId, "开始下载");
        ClientMain.Notify("正在下载: " + _name + " - " + _artist);
        string u = _url;
        lock (Sync) { _downloading = true; }
        ThreadPool.QueueUserWorkItem(delegate { DownloadAndPlay(trackId, path, 0, u); });
    }

    private static void DownloadAndPlay(long trackId, string path, int retry, string serverUrl)
    {
        try
        {
            string url = serverUrl;
            if (string.IsNullOrEmpty(url))
            {
                ClientMain.Log("服务端没给直链，客户端自己解析 id=" + trackId + "（第 " + (retry + 1) + " 次）");
                url = FetchUrl(trackId);
                ClientMain.Log("自解析结果: " + (string.IsNullOrEmpty(url) ? "(空)" : url.Substring(0, Math.Min(80, url.Length)) + "..."));
            }
            else
            {
                ClientMain.Log("使用服务端直链下载（第 " + (retry + 1) + " 次）");
            }
            if (string.IsNullOrEmpty(url)) { throw new Exception("没拿到直链（接口限流或版权受限）"); }
            long size = DownloadToFile(url, path);
            ClientMain.Log("下载完成: " + size + " B → " + path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                if (fs.Length < 2048) { throw new Exception("文件过小"); }
                byte[] head = new byte[2];
                fs.Read(head, 0, 2);
                if (head[0] == 0x4D && head[1] == 0x5A) { throw new Exception("疑似可执行文件，拒绝播放"); }
            }
            StartPlayback(path);
            ClientMain.Notify("开始播放: " + _name + " - " + _artist);
            ClientMain.ReportEvent(2, trackId, "下载完成 " + (size / 1024) + " KB");
            TrimCache();
        }
        catch (Exception ex)
        {
            ClientMain.Log("下载/准备失败(第 " + (retry + 1) + " 次): " + ex.GetType().Name + ": " + ex.Message);
            if (retry < 1)   // ★ 只重试 1 次（原来 3 次≈8 秒，一首卡住会把队列拖死）；失败尽快上报让服务端跳下一首
            {
                Thread.Sleep(1200);
                DownloadAndPlay(trackId, path, retry + 1, retry >= 1 ? "" : serverUrl);   // 服务端直链失败后改用客户端自解析
                return;
            }
            // ★ 只有「失败的就是当前曲，而且当前没有在播」时才删文件 + 上报 FINISHED；
            //   否则旧任务的失败会把正在播的歌打断（实测现象）
            long cur; bool playingNow;
            lock (Sync) { cur = _trackId; playingNow = _playing; _downloading = false; }
            if (cur != trackId || playingNow)
            {
                ClientMain.Log("忽略过期失败（失败 id=" + trackId + " 当前 id=" + cur + " 在播=" + playingNow + "）: " + ex.Message);
                return;
            }
            ClientMain.Notify("播放失败: " + ex.Message);
            ClientMain.ReportEvent(3, trackId, "下载失败: " + ex.Message);
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
            ClientMain.Log("放弃并上报 FINISHED id=" + trackId);
            ClientMain.SendUp(ClientMain.UpFinished, delegate(BinaryWriter w) { w.Write(trackId); });
        }
    }

    private static string FetchUrl(long trackId)
    {
        string json = HttpGetString(Api + "?types=url&source=netease&id=" + trackId + "&br=320", 15000);
        return JsonString(json, "url");
    }

    /// <summary>带超时的 GET（WebClient 没有超时，出问题会一直挂住）。</summary>
    private static string HttpGetString(string url, int timeoutMs)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET";
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        req.UserAgent = "Mozilla/5.0";
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            return sr.ReadToEnd();
        }
    }

    /// <summary>从接口 JSON 里取某个字符串字段（不引入额外序列化依赖）。</summary>
    private static string JsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) { return ""; }
        string pat = "\"" + key + "\"";
        int i = json.IndexOf(pat, StringComparison.Ordinal);
        if (i < 0) { return ""; }
        i = json.IndexOf(':', i + pat.Length);
        if (i < 0) { return ""; }
        i = json.IndexOf('"', i + 1);
        if (i < 0) { return ""; }
        var sb = new StringBuilder();
        for (int p = i + 1; p < json.Length; p++)
        {
            char ch = json[p];
            if (ch == '\\' && p + 1 < json.Length) { sb.Append(json[++p]); continue; }
            if (ch == '"') { break; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static long DownloadToFile(string url, string path)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "GET";
        req.Timeout = 20000;
        req.ReadWriteTimeout = 30000;
        req.UserAgent = "Mozilla/5.0";
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (Stream rs = resp.GetResponseStream())
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            byte[] buf = new byte[65536];
            long total = 0;
            int n;
            while ((n = rs.Read(buf, 0, buf.Length)) > 0) { fs.Write(buf, 0, n); total += n; }
            return total;
        }
    }

    private static void StartPlayback(string path)
    {
        lock (Sync)
        {
            _downloading = false;
            _file = path;
            _pendingPlay = true;
            _playing = false;
            _wasPlaying = false;
        }
    }

    internal static void Stop()
    {
        StopInternal(true);
    }

    internal static void StopAll()
    {
        bool had;
        lock (Sync)
        {
            had = _playing || _pendingPlay || _file != null || _pendingStop;
            _trackId = -1; _file = null; _name = ""; _artist = ""; _url = "";
        }
        if (had) { StopInternal(true); }   // ★ 幂等：菜单里每帧调用也不会刷屏 MCI stop/close
    }

    private static void StopInternal(bool mci)
    {
        lock (Sync)
        {
            _playing = false;
            _wasPlaying = false;
            _pendingPlay = false;
            _downloading = false;
            _file = null;
            _offsetMs = 0;
            if (mci) { _pendingStop = true; }
        }
    }

    internal static void SetVolume(int pct)
    {
        lock (Sync) { _volume = pct < 0 ? 0 : (pct > 100 ? 100 : pct); }
    }

    internal static void Update()
    {
        bool pendingStop, pendingPlay, playing;
        string file;
        int offset, vol;
        long trackId, startedAt;
        lock (Sync)
        {
            pendingStop = _pendingStop; _pendingStop = false;
            pendingPlay = _pendingPlay;
            file = _file;
            offset = _offsetMs;
            vol = _volume;
            trackId = _trackId;
            startedAt = _startedAtMs;
            playing = _playing;
        }

        if (pendingStop)
        {
            Send("stop rymusic");
            Send("close rymusic");
        }

        if (pendingPlay && file != null)
        {
            lock (Sync) { _pendingPlay = false; }
            try
            {
                Send("close rymusic");
                string e1 = Send("open \"" + file + "\" type mpegvideo alias rymusic");
                int startAt = offset > 0 ? offset + (int)(NowMs - startedAt) : 0;
                string e2 = Send(startAt > 0 ? ("play rymusic from " + startAt) : "play rymusic");
                ClientMain.Log("MCI 起播: open=" + (e1.Length == 0 ? "OK" : e1) + " | play=" + (e2.Length == 0 ? "OK" : e2) + " 起始=" + startAt + "ms");
                ClientMain.ReportEvent(e2.Length == 0 ? (byte)4 : (byte)5, trackId, e2.Length == 0 ? "播放中" : ("起播失败: " + e2));
                lock (Sync) { _playing = e2.Length == 0; _wasPlaying = false; }
                playing = _playing;
                TryStartPrefetch();   // 当前曲起播后再去预取下一首
            }
            catch (Exception ex)
            {
                ClientMain.Notify("初始化播放器失败: " + ex.Message);
                lock (Sync) { _playing = false; }
                return;
            }
        }

        if (!playing) { return; }

        // 音量：服务端音量 × 游戏内音乐音量
        try
        {
            float gv = Main.musicVolume;
            if (gv < 0f) { gv = 0f; }
            if (gv > 1f) { gv = 1f; }
            int v = (int)(vol * 10 * gv);
            Send("setaudio rymusic volume to " + v);
        }
        catch { }

        string mode = Query("status rymusic mode");
        if (_lastModeLogged != mode) { _lastModeLogged = mode; ClientMain.Log("MCI mode = " + mode); }
        if (mode == "playing")
        {
            lock (Sync) { _wasPlaying = true; }
        }
        else if (mode == "stopped" && _wasPlaying)
        {
            string nm = _name, ar = _artist;
            StopInternal(true);
            ClientMain.Log("播放结束，上报 FINISHED id=" + trackId);
            ClientMain.SendUp(ClientMain.UpFinished, delegate(BinaryWriter w) { w.Write(trackId); });
            ClientMain.Notify("播放结束: " + nm + " - " + ar);
            return;
        }

        long now = NowMs;
        if (now - _lastReportMs >= 2000)
        {
            _lastReportMs = now;
            long pos = 0;
            long.TryParse(Query("status rymusic position"), out pos);
            if (pos < 0) { pos = 0; }
            ClientMain.Log("上报进度 id=" + trackId + " pos=" + pos + "ms");
            ClientMain.SendUp(ClientMain.UpPosition, delegate(BinaryWriter w) { w.Write(trackId); w.Write((int)pos); });
        }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern bool mciGetErrorString(int err, StringBuilder buffer, int bufferSize);

    /// <summary>执行 MCI 命令并返回错误描述（空 = 成功），同时写日志。</summary>
    private static string Send(string cmd)
    {
        try
        {
            int e = mciSendString(cmd, null, 0, IntPtr.Zero);
            if (e == 0) { return ""; }
            var sb = new StringBuilder(256);
            try { mciGetErrorString(e, sb, sb.Capacity); } catch { }
            string msg = "MCI[" + cmd + "] 失败 " + e + ": " + sb;
            ClientMain.Log(msg);
            return msg;
        }
        catch (Exception ex) { ClientMain.Log("MCI 异常[" + cmd + "]: " + ex.Message); return ex.Message; }
    }

    private static string Query(string cmd)
    {
        try
        {
            var sb = new StringBuilder(128);
            mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            return sb.ToString().Trim().ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>缓存只保留最近 100 首，避免无限膨胀。</summary>
    private static void TrimCache()
    {
        try
        {
            string[] files = Directory.GetFiles(CacheDir(), "*.mp3");
            if (files.Length <= 120) { return; }
            Array.Sort(files, delegate(string a, string b) { return File.GetLastWriteTime(a).CompareTo(File.GetLastWriteTime(b)); });
            for (int i = 0; i < files.Length - 100; i++) { try { File.Delete(files[i]); } catch { } }
        }
        catch { }
    }
}
