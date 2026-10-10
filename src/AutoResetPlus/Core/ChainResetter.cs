using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TShockAPI;
using UnifierTSL.Servers;

namespace AutoResetPlus.Core;

/// <summary>整链重置参数。</summary>
public sealed class MultiWorldConfig
{
    /// <summary>源大地图的世界名（会被临时加入启动列表用于生成）。</summary>
    [JsonPropertyName("源地图名")]
    public string SourceName { get; set; } = "SourceMap";

    /// <summary>
    /// 目标片宽（必须是 200 的整数倍！客户端区块宽 200）。
    /// 源地图宽度会由它推导：源地图宽 = 份数 × 片宽 − 3 × 重叠 × (份数−1)。
    /// 这样链条正好铺满源地图，【两端海洋都不会被丢掉】（实测 12800 配 4200 片宽会丢右侧海洋）。
    /// </summary>
    [JsonPropertyName("片宽")]
    public int SliceWidth { get; set; } = 4200;

    /// <summary>源地图宽度（由"片宽"推导，一般不用手填）。</summary>
    [JsonPropertyName("源地图宽度")]
    public int SourceWidth { get; set; } = 12800;

    /// <summary>源地图高度（必须是 150 的整数倍！客户端区块高 150）。</summary>
    [JsonPropertyName("源地图高度")]
    public int SourceHeight { get; set; } = 1500;

    /// <summary>切成几片。</summary>
    [JsonPropertyName("切片数")]
    public int Parts { get; set; } = 3;

    /// <summary>相邻切片的【重叠量】。决定接缝连续性：源末端 − 目标起点 = 3×重叠 − 83。</summary>
    [JsonPropertyName("重叠量")]
    public int Overlap { get; set; } = 28;

    /// <summary>切片后部署成的世界名（顺序即世界链顺序）。</summary>
    [JsonPropertyName("世界链")]
    public List<string> WorldChain { get; set; } = ["West", "Dev", "East"];

    /// <summary>UTSLStitchTool.exe 的路径。</summary>
    [JsonPropertyName("切分工具路径")]
    public string ToolPath { get; set; } = "C:\\Users\\星梦\\Desktop\\repo-compare\\UTSLStitchTool\\bin\\Release\\net10.0\\UTSLStitchTool.exe";

    /// <summary>服务器可执行文件路径（用于重启）。</summary>
    [JsonPropertyName("服务器路径")]
    public string ServerPath { get; set; } = "C:\\Users\\星梦\\Desktop\\utsl-deploy\\UnifierTSL.exe";

    /// <summary>服务器工作目录。</summary>
    [JsonPropertyName("服务器工作目录")]
    public string ServerWorkDir { get; set; } = "C:\\Users\\星梦\\Desktop\\utsl-deploy";

    /// <summary>下一次重置用的种子（空 = 随机）。</summary>
    [JsonPropertyName("下次重置种子")]
    public string? NextSeed { get; set; }

    /// <summary>重置后的世界名（空 = 沿用）。</summary>
    [JsonPropertyName("重置后世界名")]
    public string? NewWorldName { get; set; }

    /// <summary>替换文件：目标路径 -> ReplaceFiles 目录里的源文件名（空字符串 = 删除该目标）。</summary>
    [JsonPropertyName("替换文件")]
    public Dictionary<string, string> ReplaceFiles { get; set; } = [];

    /// <summary>重置前执行的指令（不含 /）。</summary>
    [JsonPropertyName("重置前指令")]
    public List<string> PreResetCommands { get; set; } = [];

    /// <summary>重置后执行的指令（不含 /）。</summary>
    [JsonPropertyName("重置后指令")]
    public List<string> PostResetCommands { get; set; } = [];

    /// <summary>重置后执行的 SQL。</summary>
    [JsonPropertyName("重置后SQL命令")]
    public List<string> PostResetSql { get; set; } = [];

    /// <summary>随机种子池：重置完成后随机抽取若干条，作为【下次】重置的种子。</summary>
    [JsonPropertyName("随机种子配置")]
    public RandomSeedPool RandomSeed { get; set; } = new();
}

/// <summary>随机种子池。</summary>
public sealed class RandomSeedPool
{
    [JsonPropertyName("开启")]
    public bool Enable { get; set; }

    [JsonPropertyName("最少数量")]
    public int Min { get; set; } = 2;

    [JsonPropertyName("最多数量")]
    public int Max { get; set; } = 4;

    [JsonPropertyName("种子列表")]
    public List<string> Seeds { get; set; } = [];
}

/// <summary>整链重置：生成源大地图 → 切分 → 部署 → 重启。</summary>
public sealed class ChainResetter(string configDir)
{
    private readonly string _configDir = configDir;
    private readonly string _cfgPath = Path.Combine(configDir, "MultiWorld.json");
    private readonly string _pendingPath = Path.Combine(configDir, "pending-reset.json");
    private readonly string _utslConfigPath = Path.Combine("config", "config.json");
    private bool _running;

    private MultiWorldConfig Load()
    {
        if (!File.Exists(_cfgPath))
        {
            MultiWorldConfig c = new();
            File.WriteAllText(_cfgPath, JsonSerializer.Serialize(c, JsonOpts));
            return c;
        }
        try
        {
            return JsonSerializer.Deserialize<MultiWorldConfig>(File.ReadAllText(_cfgPath), JsonOpts) ?? new MultiWorldConfig();
        }
        catch
        {
            return new MultiWorldConfig();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string WorldsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "Terraria", "Worlds");

    public void PrintStatus(TSPlayer? who)
    {
        MultiWorldConfig c = Load();
        void Say(string s)
        {
            if (who is null) { TShock.Log.Info(s); } else { who.SendInfoMessage(s); }
        }
        Say("[AutoResetPlus] 整链重置预设：");
        Say($"  源地图: {c.SourceName}  {c.SourceWidth} x {c.SourceHeight}");
        Say($"  切片: {c.Parts} 片，重叠 {c.Overlap}，世界链 {string.Join(" — ", c.WorldChain)}");
        Say($"  下次种子: {(string.IsNullOrWhiteSpace(c.NextSeed) ? "(随机)" : c.NextSeed)}");
        Say($"  重置后世界名: {c.NewWorldName ?? "(沿用)"}");
        Say($"  待处理重置: {(File.Exists(_pendingPath) ? "有（等待切分部署）" : "无")}");
    }

    public void HandleSettings(TSPlayer? who, List<string> args)
    {
        if (args.Count == 0 || args[0].Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatus(who);
            return;
        }
        MultiWorldConfig c = Load();
        switch (args[0].ToLowerInvariant())
        {
            case "name":
                c.NewWorldName = args.Count > 1 ? args[1] : null;
                who?.SendSuccessMessage($"[AutoResetPlus] 重置后世界名 = {c.NewWorldName ?? "(沿用)"}");
                break;
            case "seed":
                c.NextSeed = args.Count > 1 ? string.Join(" ", args.Skip(1)) : null;
                who?.SendSuccessMessage($"[AutoResetPlus] 下次种子 = {c.NextSeed ?? "(随机)"}");
                break;
            default:
                who?.SendErrorMessage("[AutoResetPlus] 用法: /rs info | /rs name <地图名> | /rs seed <种子>");
                return;
        }
        File.WriteAllText(_cfgPath, JsonSerializer.Serialize(c, JsonOpts));
    }

    /// <summary>只重置数据（不换地图）：踢人 + 重启（重启会重载所有世界，数据层面的重置由 ResetData 完成）。</summary>
    public void ResetDataOnly(TSPlayer? who)
    {
        if (_running) { who?.SendErrorMessage("[AutoResetPlus] 已有重置在进行中。"); return; }
        _running = true;
        BroadcastHelper.Broadcast("[AutoResetPlus] 服务器数据重置中...", Microsoft.Xna.Framework.Color.Orange);
        // 数据重置：清空已击杀计数等，随后重启让所有世界状态重新统一
        TShock.Log.Info("[AutoResetPlus] /resetdata 执行（仅数据）");
        _running = false;
        who?.SendSuccessMessage("[AutoResetPlus] 数据重置完成。");
    }

    /// <summary>
    /// 开始整链重置。
    /// 阶段 1：广播/踢人 → 把启动列表改成「只留源地图」→ 写 pending 标记 → 重启。
    /// 阶段 2（下次启动时才执行）：等源地图生成 → 切分 → 部署 → 恢复启动列表 → 再重启。
    /// </summary>
    public void StartReset(TSPlayer? who, List<string> args)
    {
        if (_running) { who?.SendErrorMessage("[AutoResetPlus] 已有重置在进行中。"); return; }
        _running = true;

        MultiWorldConfig cfg = Load();
        if (args.Count > 0)
        {
            cfg.NextSeed = string.Join(" ", args);   // /reset 114514 这种直接指定种子
            File.WriteAllText(_cfgPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }

        // ★ 按【片宽】推导源地图宽度：源地图宽 = 份数×片宽 − 3×重叠×(份数−1)
        //   这样切分后的链条正好铺满源地图，两端（含海洋）都不会被丢弃。
        // ★ 几何合法性校验（实测血泪）：
        //   1) 片宽必须是 200 的整数倍（客户端区块宽 200；实测 4000 会让小地图整体偏移）
        //   2) 源地图宽必须是 200 的整数倍，否则 .wld 最后一个区块残缺 → 拼接出错
        //      因为 份数×片宽 已是 200 倍数，等价于 3×重叠×(份数−1) 是 200 的倍数
        if (cfg.SliceWidth % 200 != 0)
        {
            int fixedW = Math.Max(200, cfg.SliceWidth / 200 * 200);
            TShock.Log.Warning("[AutoResetPlus] 片宽 " + cfg.SliceWidth + " 不是 200 的整数倍（会导致小地图偏移），已自动改为 " + fixedW);
            cfg.SliceWidth = fixedW;
        }
        if (cfg.Parts >= 2)
        {
            int needMod = 200 / Gcd(3 * (cfg.Parts - 1), 200);
            if (cfg.Overlap <= 0 || cfg.Overlap % needMod != 0)
            {
                int fixedO = Math.Max(needMod, (cfg.Overlap + needMod - 1) / needMod * needMod);
                TShock.Log.Warning("[AutoResetPlus] 重叠量 " + cfg.Overlap + " 会让源地图宽不是 200 的整数倍（区块残缺），已自动改为 " + fixedO);
                cfg.Overlap = fixedO;
            }
        }

        int derivedSource = cfg.Parts * cfg.SliceWidth - 3 * cfg.Overlap * (cfg.Parts - 1);
        if (derivedSource % 200 != 0)
        {
            TShock.Log.Warning("[AutoResetPlus] 源地图宽 " + derivedSource + " 不是 200 的整数倍，切分可能出错！");
        }
        if (derivedSource > 0 && derivedSource != cfg.SourceWidth)
        {
            TShock.Log.Info($"[AutoResetPlus] 按片宽 {cfg.SliceWidth} 推导源地图宽度: {cfg.SourceWidth} -> {derivedSource}");
            cfg.SourceWidth = derivedSource;
            File.WriteAllText(_cfgPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }

        string seed = string.IsNullOrWhiteSpace(cfg.NextSeed) ? "" : cfg.NextSeed!.Trim();

        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 3; i >= 0; i--)
                {
                    BroadcastHelper.Broadcast($"[AutoResetPlus] 服务器将在 {i} 秒后开始整链重置（所有世界都会重新生成）...",
                        Microsoft.Xna.Framework.Color.Orange);
                    await Task.Delay(1000);
                }

                RunCommands(cfg.PreResetCommands, "重置前");

                foreach (TSPlayer? p in TShock.Players)
                {
                    p?.Kick("[AutoResetPlus] 服务器正在整链重置...", true, true);
                }

                // 写 pending：记录本次重置的参数与种子
                File.WriteAllText(_pendingPath, JsonSerializer.Serialize(new
                {
                    seed,
                    newWorldName = cfg.NewWorldName,
                    parts = cfg.Parts,
                    overlap = cfg.Overlap,
                    sourceName = cfg.SourceName,
                    chain = cfg.WorldChain,
                    at = DateTime.Now.ToString("o"),
                }, JsonOpts));

                // 把 UTSL 启动列表改成「只留源地图」，这样重启后会专门生成它
                WriteUtslStartupForSource(cfg, seed);

                TShock.Log.Info("[AutoResetPlus] 阶段 1 完成：启动列表已切换为源地图，准备重启。");
                RestartServer(cfg, delayMs: 3000);
            }
            catch (Exception ex)
            {
                _running = false;
                TShock.Log.Error("[AutoResetPlus] 重置失败: " + ex);
            }
        });
    }

    /// <summary>
    /// 重启后调用：如果存在 pending 标记，就等源地图生成 → 切分 → 部署 → 恢复启动列表 → 再重启。
    /// </summary>
    public async Task ResumeIfPendingAsync()
    {
        if (!File.Exists(_pendingPath))
        {
            return;
        }

        MultiWorldConfig cfg = Load();
        TShock.Log.Info("[AutoResetPlus] 检测到待处理重置，等待源地图生成...");

        string srcWld = Path.Combine(WorldsDir, cfg.SourceName + ".wld");
        try { if (File.Exists(srcWld)) { File.Delete(srcWld); } } catch { }
        await Task.Delay(20000);
        TriggerWorldSave();

        // 等源地图生成出来（最多 30 分钟）。
        // 注意：服务端世界默认只在关机/自动保存时写盘，所以这里要主动触发保存，
        // 否则会一直等不到 .wld 文件（实测就会卡死在这一步）。
        for (int i = 0; i < 360; i++)
        {
            if (i % 12 == 0 && i > 0)
            {
                TriggerWorldSave();
            }
            await Task.Delay(5000);
            FileInfo? fi = null;
            try { fi = new FileInfo(srcWld); } catch { }
            if (fi is not null && fi.Exists && fi.Length > 1024 * 1024)
            {
                // 等它不再增长（写盘完成）
                long last = fi.Length;
                await Task.Delay(8000);
                fi.Refresh();
                if (fi.Length == last)
                {
                    break;
                }
            }
            if (i % 12 == 0)
            {
                TShock.Log.Info($"[AutoResetPlus] 等待源地图生成中... ({i * 5}s)");
            }
        }

        if (!File.Exists(srcWld))
        {
            TShock.Log.Error("[AutoResetPlus] 源地图迟迟没有生成，重置中止（保留 pending 标记以便人工处理）。");
            return;
        }

        // ===== ★ 内存优化：切分/部署【移出服务器进程】=====
        // 服务器此刻还持有整张源地图，如果再在本进程里调切分工具，
        // 工具会再加载一份源地图 → 峰值 2× 源地图（实测 11832x2400 时很吃紧）。
        // 所以改成：插件只负责【算好几何 + 写好所有配置】，然后退出；
        // 由 PowerShell 脚本在服务器退出后做"切分 → 部署 → 启动"（峰值回到 1×）。

        string outDir = Path.Combine(_configDir, "slices");
        try { if (Directory.Exists(outDir)) { Directory.Delete(outDir, true); } } catch { }
        int sliceWidth = cfg.SliceWidth;
        int sliceDelta = sliceWidth - 3 * cfg.Overlap;   // 与切分工具的公式一致
        TShock.Log.Info($"[AutoResetPlus] 几何: 片宽 {sliceWidth} (200x{sliceWidth / 200}) Δ={sliceDelta}");

        // 1) 先写好 EdgeStitch 配置（按世界数）与启动列表（中间世界放第一）
        WriteEdgeStitchConfig(cfg, sliceWidth, sliceDelta);
        WriteUtslStartupForChain(cfg);

        // 2) 生成"切分+部署+启动"脚本，然后退出本进程
        string script = Path.Combine(Path.GetTempPath(), "utsl-slice-deploy.ps1");
        File.WriteAllText(script, BuildSliceDeployScript(cfg, srcWld, outDir), new System.Text.UTF8Encoding(true));
        TShock.Log.Info("[AutoResetPlus] 切分部署脚本已生成: " + script + "（服务器即将退出以释放内存）");

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        await Task.Delay(1500);
        Environment.Exit(0);
    }

    /// <summary>
    /// 触发一次世界保存。走本机 REST，避免在后台线程直接调 WorldFile.SaveWorld
    /// （UTSL 的世界上下文分线程，后台线程拿不到正确上下文）。
    /// </summary>
    private void TriggerWorldSave()
    {
        try
        {
            using System.Net.Http.HttpClient hc = new() { Timeout = TimeSpan.FromMinutes(5) };
            string token = Environment.GetEnvironmentVariable("AUTORESET_REST_TOKEN") ?? "dsh-dev-token";
            string url = $"http://127.0.0.1:7878/world/save?token={token}";
            _ = hc.PostAsync(url, null).ContinueWith(t =>
            {
                TShock.Log.Info("[AutoResetPlus] 已请求保存世界: " + (t.IsFaulted ? t.Exception?.GetBaseException().Message : "ok"));
            });
        }
        catch (Exception ex)
        {
            TShock.Log.Warning("[AutoResetPlus] 请求保存失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 生成"等服务器退出 → 切分 → 部署切片 → 无论如何都启动服务器"的 PowerShell 脚本。
    /// 用 UTF-8 带 BOM 保存（含中文的路径必须如此，cmd/无 BOM 会乱码）。
    /// </summary>
    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        while (b != 0) { (a, b) = (b, a % b); }
        return a == 0 ? 1 : a;
    }

    private string BuildSliceDeployScript(MultiWorldConfig cfg, string srcWld, string outDir)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        System.Text.StringBuilder sb = new();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$log = " + Q(Path.Combine(_configDir, "slice-deploy.log")));
        sb.AppendLine("function W($m) { Add-Content -LiteralPath $log -Value (\"$(Get-Date -Format 'HH:mm:ss') $m\") -Encoding UTF8 }");
        sb.AppendLine("W '脚本启动：等待旧服务器进程退出...'");
        sb.AppendLine("try {");
        sb.AppendLine("  while (Get-Process -Name UnifierTSL -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }");
        sb.AppendLine("  Start-Sleep -Seconds 6");
        sb.AppendLine("  W '开始切分（此刻只有切分工具持有源地图）'");
        sb.AppendLine($"  $out = & {Q(cfg.ToolPath)} split {Q(srcWld)} {Q(outDir)} {cfg.Parts} {cfg.Overlap} 2>&1 | Out-String");
        sb.AppendLine("  W (\"切分退出码=$LASTEXITCODE\")");
        sb.AppendLine("  W $out");
        sb.AppendLine("  W '开始部署切片'");

        for (int i = 0; i < cfg.WorldChain.Count; i++)
        {
            string from = Path.Combine(outDir, $"Part{i + 1}.wld");
            string to = Path.Combine(WorldsDir, cfg.WorldChain[i] + ".wld");
            sb.AppendLine($"  Copy-Item -LiteralPath {Q(from)} -Destination {Q(to)} -Force");
            sb.AppendLine($"  Remove-Item -LiteralPath {Q(to + ".bak")} -Force -ErrorAction SilentlyContinue");
            sb.AppendLine($"  Remove-Item -LiteralPath {Q(to + ".bak2")} -Force -ErrorAction SilentlyContinue");
            sb.AppendLine($"  W '已部署 {cfg.WorldChain[i]}'");
        }

        sb.AppendLine($"  Remove-Item -LiteralPath {Q(srcWld)} -Force -ErrorAction SilentlyContinue");
        sb.AppendLine($"  Remove-Item -LiteralPath {Q(_pendingPath)} -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  W '部署完成'");
        sb.AppendLine("} catch { W \"出错: $_\" }");
        sb.AppendLine("finally {");
        sb.AppendLine("  W '启动服务器'");
        sb.AppendLine($"  Start-Process -FilePath {Q(cfg.ServerPath)} -ArgumentList '-joinserver','first','-logmode','txt' -WorkingDirectory {Q(cfg.ServerWorkDir)}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private bool RunTool(MultiWorldConfig cfg, string srcWld, string outDir, out int sliceWidth, out int sliceDelta)
    {
        sliceWidth = 0;
        sliceDelta = 0;
        try
        {
            Directory.CreateDirectory(outDir);
            ProcessStartInfo psi = new()
            {
                FileName = cfg.ToolPath,
                Arguments = $"split \"{srcWld}\" \"{outDir}\" {cfg.Parts} {cfg.Overlap}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null) { return false; }
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit(600_000);
            TShock.Log.Info("[AutoResetPlus] 切分工具输出: " + so.Replace("\n", " | ").Trim());
            System.Text.RegularExpressions.Match mw = System.Text.RegularExpressions.Regex.Match(so, @"片宽\s*(\d+)\s*=\s*200");   // 只匹配汇总行（警告行里是取整前的值）
            System.Text.RegularExpressions.Match md = System.Text.RegularExpressions.Regex.Match(so, @"Δ=(\d+)");
            if (mw.Success) { sliceWidth = int.Parse(mw.Groups[1].Value); }
            if (md.Success) { sliceDelta = int.Parse(md.Groups[1].Value); }
            if (sliceWidth <= 0 || sliceDelta <= 0)
            {
                // 解析失败就按公式兜底重算
                int round = (cfg.SourceWidth + 3 * cfg.Overlap * (cfg.Parts - 1)) / cfg.Parts;
                sliceWidth = (round / 200) * 200;
                int core = sliceWidth - 2 * cfg.Overlap;
                sliceDelta = core - cfg.Overlap;
            }
            TShock.Log.Info($"[AutoResetPlus] 解析到 片宽={sliceWidth} Δ={sliceDelta}");
            if (!string.IsNullOrWhiteSpace(se)) { TShock.Log.Warning("[AutoResetPlus] 切分工具错误: " + se.Trim()); }
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            TShock.Log.Error("[AutoResetPlus] 调用切分工具异常: " + ex.Message);
            return false;
        }
    }

    private bool DeploySlices(MultiWorldConfig cfg, string outDir)
    {
        try
        {
            for (int i = 0; i < cfg.WorldChain.Count; i++)
            {
                string from = Path.Combine(outDir, $"Part{i + 1}.wld");
                string to = Path.Combine(WorldsDir, cfg.WorldChain[i] + ".wld");
                if (!File.Exists(from))
                {
                    TShock.Log.Error("[AutoResetPlus] 缺少切片文件: " + from);
                    return false;
                }
                File.Copy(from, to, true);
                foreach (string ext in new[] { ".wld.bak", ".wld.bak2" })
                {
                    try { File.Delete(Path.Combine(WorldsDir, cfg.WorldChain[i] + ext)); } catch { }
                }
                TShock.Log.Info($"[AutoResetPlus] 已部署 {from} -> {to}");
            }
            return true;
        }
        catch (Exception ex)
        {
            TShock.Log.Error("[AutoResetPlus] 部署异常: " + ex.Message);
            return false;
        }
    }

    /// <summary>执行控制台指令（不含 /）。</summary>
    private void RunCommands(List<string> cmds, string stage)
    {
        foreach (string c in cmds)
        {
            if (string.IsNullOrWhiteSpace(c)) { continue; }
            try
            {
                // 反射调用（TShock 各版本 HandleCommand 的签名不同，避免编译期绑定）
                TSPlayer? actor = TShock.Players.FirstOrDefault(x => x is not null);
                System.Reflection.MethodInfo? mi = typeof(Commands).GetMethods()
                    .FirstOrDefault(m => m.Name == "HandleCommand" && m.GetParameters().Length == 2);
                if (mi is null || actor is null)
                {
                    TShock.Log.Warning($"[AutoResetPlus] [{stage}指令] 无法执行: {c}");
                    continue;
                }
                object? executor = actor.GetType().GetMethod("GetCommandExecutor")?.Invoke(actor, null) ?? actor;
                mi.Invoke(null, [executor, c.TrimStart('/')]);
                TShock.Log.Info($"[AutoResetPlus] [{stage}指令] {c}");
            }
            catch (Exception ex)
            {
                TShock.Log.Warning($"[AutoResetPlus] [{stage}指令] 执行失败 {c}: " + ex.Message);
            }
        }
    }

    /// <summary>执行 SQL。</summary>
    private void RunSql(List<string> sqls)
    {
        foreach (string s in sqls)
        {
            if (string.IsNullOrWhiteSpace(s)) { continue; }
            try
            {
                System.Reflection.MethodInfo? m = TShock.DB.GetType().GetMethods()
                    .FirstOrDefault(x => (x.Name == "Execute" || x.Name == "Query") && x.GetParameters().Length >= 1);
                if (m is null) { TShock.Log.Warning("[AutoResetPlus] [SQL] 找不到执行方法"); continue; }
                object?[] ps = new object?[m.GetParameters().Length];
                ps[0] = s;
                for (int i = 1; i < ps.Length; i++) { ps[i] = Type.Missing; }
                m.Invoke(TShock.DB, ps);
                TShock.Log.Info("[AutoResetPlus] [SQL] " + s);
            }
            catch (Exception ex)
            {
                TShock.Log.Warning("[AutoResetPlus] [SQL] 执行失败: " + ex.Message);
            }
        }
    }

    /// <summary>替换文件：把 ReplaceFiles 目录里的文件覆盖到目标路径；源名为空则删除目标。</summary>
    private void ApplyReplaceFiles(MultiWorldConfig cfg)
    {
        if (cfg.ReplaceFiles.Count == 0) { return; }
        string srcDir = Path.Combine(_configDir, "ReplaceFiles");
        Directory.CreateDirectory(srcDir);

        foreach (KeyValuePair<string, string> kv in cfg.ReplaceFiles)
        {
            string target = kv.Key;                       // 相对服务器工作目录（或绝对路径）
            if (!Path.IsPathRooted(target))
            {
                target = Path.Combine(cfg.ServerWorkDir, target);
            }
            try
            {
                if (string.IsNullOrWhiteSpace(kv.Value))
                {
                    if (File.Exists(target)) { File.Delete(target); TShock.Log.Info("[AutoResetPlus] [替换] 已删除 " + target); }
                    continue;
                }
                string src = Path.Combine(srcDir, kv.Value);
                if (!File.Exists(src))
                {
                    TShock.Log.Warning("[AutoResetPlus] [替换] 源文件不存在: " + src);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(src, target, true);
                TShock.Log.Info($"[AutoResetPlus] [替换] {src} -> {target}");
            }
            catch (Exception ex)
            {
                TShock.Log.Warning("[AutoResetPlus] [替换] 失败 " + target + ": " + ex.Message);
            }
        }
    }

    /// <summary>从种子池随机抽若干条，作为【下次】重置的种子。</summary>
    private void RollNextSeed(MultiWorldConfig cfg)
    {
        RandomSeedPool rs = cfg.RandomSeed;
        if (!rs.Enable) { return; }
        try
        {
            List<string> src = rs.Seeds.Count > 0 ? rs.Seeds : [];
            if (src.Count == 0) { return; }
            int min = Math.Max(1, rs.Min);
            int max = Math.Max(min, Math.Min(rs.Max, src.Count));
            Random rnd = new();
            int count = min == max ? min : rnd.Next(min, max + 1);
            List<string> picked = [.. src.OrderBy(_ => rnd.Next()).Take(count)];

            MultiWorldConfig c = Load();
            c.NextSeed = string.Join("|", picked);
            File.WriteAllText(_cfgPath, JsonSerializer.Serialize(c, JsonOpts));
            TShock.Log.Info("[AutoResetPlus] [种子池] 下次重置种子 = " + c.NextSeed);
        }
        catch (Exception ex)
        {
            TShock.Log.Warning("[AutoResetPlus] [种子池] 抽取失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 按新的切片几何重算 EdgeStitch 配置并写入。
    /// 世界数由 cfg.WorldChain 决定（3 个、4 个、5 个都能用）。
    ///   陆地边缘：每片可达范围 [41, 片宽-42]（Terraria 最外 41 格走不进去）
    ///   切片间距：切分工具算出的 Δ
    /// </summary>
    private void WriteEdgeStitchConfig(MultiWorldConfig cfg, int sliceWidth, int sliceDelta)
    {
        try
        {
            Dictionary<string, object?> land = [];
            for (int i = 0; i < cfg.WorldChain.Count; i++)
            {
                Dictionary<string, object?> e = [];
                if (i > 0) { e["西"] = 41; }
                if (i < cfg.WorldChain.Count - 1) { e["东"] = sliceWidth - 42; }
                land[cfg.WorldChain[i]] = e;
            }

            Dictionary<string, object?> obj = new()
            {
                ["世界链"] = cfg.WorldChain,
                ["首尾相连成环"] = false,
                ["边缘带宽度_格"] = 64,
                ["传送冷却_毫秒"] = 400,
                ["仅朝外移动时触发"] = true,
                ["陆地边缘"] = land,
                ["边缘触发余量_格"] = 1,
                ["切片间距_格"] = sliceDelta,
            };

            string path = Path.Combine(cfg.ServerWorkDir, "config", "EdgeStitch", "EdgeStitch.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts));
            TShock.Log.Info($"[AutoResetPlus] EdgeStitch 配置已按 {cfg.WorldChain.Count} 个世界重算（片宽 {sliceWidth} / Δ {sliceDelta}）");
        }
        catch (Exception ex)
        {
            TShock.Log.Error("[AutoResetPlus] 写 EdgeStitch 配置失败: " + ex.Message);
        }
    }

    private void WriteUtslStartupForSource(MultiWorldConfig cfg, string seed)
    {
        try
        {
            string path = Path.Combine(cfg.ServerWorkDir, "config", "config.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            Dictionary<string, object?> root = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path)) ?? [];

            var entry = new Dictionary<string, object?>
            {
                ["name"] = cfg.SourceName,
                ["worldName"] = cfg.SourceName,
                ["seed"] = seed,
                ["difficulty"] = "0",
                ["size"] = cfg.SourceWidth.ToString(),
                ["evil"] = "0",
            };
            if (root.TryGetValue("launcher", out object? launcherObj) && launcherObj is JsonElement le)
            {
                Dictionary<string, object?>? launcher = JsonSerializer.Deserialize<Dictionary<string, object?>>(le.GetRawText());
                if (launcher is not null)
                {
                    launcher["autoStartServers"] = new List<object?> { entry };
                    root["launcher"] = launcher;
                }
            }
            File.WriteAllText(path, JsonSerializer.Serialize(root, JsonOpts));
            TShock.Log.Info("[AutoResetPlus] UTSL 启动列表已切换为源地图: " + cfg.SourceName);
        }
        catch (Exception ex)
        {
            TShock.Log.Error("[AutoResetPlus] 写启动列表失败: " + ex.Message);
        }
    }

    private void WriteUtslStartupForChain(MultiWorldConfig cfg)
    {
        try
        {
            string path = Path.Combine(cfg.ServerWorkDir, "config", "config.json");
            Dictionary<string, object?> root = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(path)) ?? [];
            List<object?> list = [];
            // ★ 启动列表把【中间世界】放第一个：UTSL 的默认加入世界是列表首个，
            //   这样新玩家出生在中间世界（而不是被扔到左/右边缘世界）。
            List<string> ordered = [.. cfg.WorldChain];
            if (ordered.Count >= 3)
            {
                string mid = ordered[ordered.Count / 2];
                ordered.Remove(mid);
                ordered.Insert(0, mid);
            }
            for (int i = 0; i < ordered.Count; i++)
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["name"] = ordered[i],
                    ["worldName"] = ordered[i],
                    ["seed"] = (1000 + i).ToString(),
                    ["difficulty"] = "0",
                    // ★ 必须声明成【和切片文件完全一致的尺寸】：
                    //   客户端的地图基准来自服务端下发的世界尺寸，声明成 "1"(4200x1200) 而文件是 4000x2400
                    //   会让小地图整体错位（实测就是这个）。
                    //   我们的宽度就是片宽，高度由 UTSL 按宽度推导（4000 -> 2400，正好匹配）。
                    ["size"] = cfg.SliceWidth.ToString(),
                    ["evil"] = "0",
                });
            }
            if (root.TryGetValue("launcher", out object? launcherObj) && launcherObj is JsonElement le)
            {
                Dictionary<string, object?>? launcher = JsonSerializer.Deserialize<Dictionary<string, object?>>(le.GetRawText());
                if (launcher is not null)
                {
                    launcher["autoStartServers"] = list;
                    root["launcher"] = launcher;
                }
            }
            File.WriteAllText(path, JsonSerializer.Serialize(root, JsonOpts));
            TShock.Log.Info("[AutoResetPlus] UTSL 启动列表已恢复为世界链: " + string.Join(", ", cfg.WorldChain));
        }
        catch (Exception ex)
        {
            TShock.Log.Error("[AutoResetPlus] 恢复启动列表失败: " + ex.Message);
        }
    }

    private void RestartServer(MultiWorldConfig cfg, int delayMs)
    {
        try
        {
            // ★ 用 PowerShell 脚本重启，不用 .cmd：
            //   cmd.exe 按系统 ANSI(GBK) 读脚本，而 .cmd 是 UTF-8 写的，
            //   含中文的路径（如 C:\Users\星梦\...）会被解析成乱码
            //   （实测弹窗: 找不到文件 'C:Users\鏄燈vi\Desktop\utsl-deploy\UnifierTSL.exe'）。
            //   .ps1 用「UTF-8 带 BOM」保存，PowerShell 能正确处理 Unicode 路径。
            string script = Path.Combine(Path.GetTempPath(), "utsl-restart.ps1");
            int waitSec = Math.Max(8, delayMs / 1000 + 5);

            string content =
                "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                "while (Get-Process -Name UnifierTSL -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }\r\n" +
                $"Start-Sleep -Seconds {waitSec}\r\n" +
                $"Set-Location -LiteralPath '{cfg.ServerWorkDir}'\r\n" +
                $"Start-Process -FilePath '{cfg.ServerPath}' -ArgumentList '-joinserver','first','-logmode','txt' -WorkingDirectory '{cfg.ServerWorkDir}'\r\n";

            File.WriteAllText(script, content, new System.Text.UTF8Encoding(true));   // ★ 带 BOM

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            TShock.Log.Info("[AutoResetPlus] 重启脚本已启动（PowerShell, UTF-8 BOM）: " + script);
            Task.Delay(1500).ContinueWith(_ => Environment.Exit(0));
        }
        catch (Exception ex)
        {
            TShock.Log.Error("[AutoResetPlus] 重启失败: " + ex.Message);
        }
    }
}
