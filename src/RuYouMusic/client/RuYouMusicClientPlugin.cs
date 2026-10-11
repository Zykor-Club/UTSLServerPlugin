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

    internal const byte UpHello = 1, UpFinished = 3, UpPosition = 4;
    internal const byte DnProbe = 1, DnPlay = 2, DnStop = 3, DnVolume = 4, DnSession = 5;

    internal static readonly byte[] Magic = { 0x52, 0x59, 0x4D, 0x31 };
    internal static readonly byte[] Token = new byte[8];
    internal static volatile bool Authed;

    static ClientMain()
    {
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
                    Music.SetVolume(vol);
                    Music.Play(trackId, name, artist, offsetMs);
                    break;
                }
                case DnStop:
                    Music.Stop();
                    break;
                case DnVolume:
                    Music.SetVolume(r.ReadByte());
                    break;
            }
            e.Handled = true;
        }
        catch (Exception ex) { Notify("收包异常: " + ex.Message); }
    }

    private static void OnPlayerUpdate() { try { Music.Update(); } catch { } }

    private static void OnPreUpdate(HandledEventArgs e)
    {
        try
        {
            if (Main.gameMenu) { Authed = false; Music.StopAll(); }
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

    internal static string ReadStr(BinaryReader r)
    {
        int n = r.ReadUInt16();
        byte[] b = r.ReadBytes(n);
        return Encoding.UTF8.GetString(b);
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

    private static long NowMs { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; } }

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

    internal static void Play(long trackId, string name, string artist, int offsetMs)
    {
        StopInternal(false);
        lock (Sync)
        {
            _trackId = trackId;
            _name = name == null ? "" : name;
            _artist = artist == null ? "" : artist;
            _offsetMs = Math.Max(0, offsetMs);
            _startedAtMs = NowMs;
        }
        string path = Path.Combine(CacheDir(), trackId + ".mp3");
        if (File.Exists(path) && new FileInfo(path).Length > 1024)
        {
            StartPlayback(path);
            ClientMain.Notify("开始播放: " + _name + " - " + _artist);
            return;
        }
        ClientMain.Notify("正在下载: " + _name + " - " + _artist);
        ThreadPool.QueueUserWorkItem(delegate { DownloadAndPlay(trackId, path, 0); });
    }

    private static void DownloadAndPlay(long trackId, string path, int retry)
    {
        try
        {
            string url = FetchUrl(trackId);
            if (string.IsNullOrEmpty(url)) { throw new Exception("接口未返回直链（可能版权受限）"); }
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                wc.DownloadFile(url, path);
            }
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                if (f.Length < 2048) { throw new Exception("文件过小"); }
                byte[] head = new byte[2];
                f.Read(head, 0, 2);
                if (head[0] == 0x4D && head[1] == 0x5A) { throw new Exception("疑似可执行文件，拒绝播放"); }
            }
            StartPlayback(path);
            ClientMain.Notify("开始播放: " + _name + " - " + _artist);
            TrimCache();
        }
        catch (Exception ex)
        {
            if (retry < 3) { Thread.Sleep(1000); DownloadAndPlay(trackId, path, retry + 1); return; }
            ClientMain.Notify("播放失败: " + ex.Message);
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
            long id; lock (Sync) { id = _trackId; }
            ClientMain.SendUp(ClientMain.UpFinished, delegate(BinaryWriter w) { w.Write(id); });   // 让服务端跳到下一首
        }
    }

    private static string FetchUrl(long trackId)
    {
        using (var wc = new WebClient())
        {
            wc.Headers.Add("User-Agent", "Mozilla/5.0");
            string json = wc.DownloadString(Api + "?types=url&source=netease&id=" + trackId + "&br=320");
            return JsonString(json, "url");
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
            char c = json[p];
            if (c == '\\' && p + 1 < json.Length) { sb.Append(json[++p]); continue; }
            if (c == '"') { break; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void StartPlayback(string path)
    {
        lock (Sync)
        {
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
        StopInternal(true);
        lock (Sync) { _trackId = -1; _file = null; _name = ""; _artist = ""; }
    }

    private static void StopInternal(bool mci)
    {
        lock (Sync)
        {
            _playing = false;
            _wasPlaying = false;
            _pendingPlay = false;
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
                Send("open \"" + file + "\" type mpegvideo alias rymusic");
                int startAt = offset > 0 ? offset + (int)(NowMs - startedAt) : 0;
                Send(startAt > 0 ? ("play rymusic from " + startAt) : "play rymusic");
                lock (Sync) { _playing = true; _wasPlaying = false; }
                playing = true;
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
        if (mode == "playing")
        {
            lock (Sync) { _wasPlaying = true; }
        }
        else if (mode == "stopped" && _wasPlaying)
        {
            string nm = _name, ar = _artist;
            StopInternal(true);
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
            ClientMain.SendUp(ClientMain.UpPosition, delegate(BinaryWriter w) { w.Write(trackId); w.Write((int)pos); });
        }
    }

    private static void Send(string cmd) { try { mciSendString(cmd, null, 0, IntPtr.Zero); } catch { } }

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
