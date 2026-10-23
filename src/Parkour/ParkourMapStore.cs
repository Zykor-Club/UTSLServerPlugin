using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parkour;

/// <summary>
/// 地图存取：config/Parkour/maps/&lt;id&gt;.json（一个图一个文件，方便手工改/备份）。
/// </summary>
internal static class ParkourMapStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string root = "";
    private static readonly object Sync = new();

    public static string Root => root;

    public static void Init(string configDir)
    {
        root = Path.Combine(configDir, "maps");
        Directory.CreateDirectory(root);
    }

    private static string PathOf(string id) => Path.Combine(root, id + ".json");

    /// <summary>读全部地图；解析失败的文件跳过并打日志（不让一张坏图拖垮插件）。</summary>
    public static List<ParkourMap> LoadAll()
    {
        List<ParkourMap> list = new();
        lock (Sync)
        {
        if (!Directory.Exists(root)) { return list; }

        foreach (string file in Directory.GetFiles(root, "*.json"))
        {
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { continue; }
            try
            {
                ParkourMap? m = ParkourMap.FromJson(File.ReadAllText(file));
                if (m is null || string.IsNullOrWhiteSpace(m.Id)) { continue; }
                list.Add(m);
            }
            catch (Exception ex)
            {
                TShockAPI.TShock.Log.Warning($"[跑酷] 地图解析失败（已跳过）{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return list;
        }
    }

    /// <summary>原子写：写 .tmp 再替换，避免并发/中断留下半截 JSON（丢图）。</summary>
    public static bool Save(ParkourMap map, out string path)
    {
        path = PathOf(map.Id);
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(root);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, map.ToJson());
                if (File.Exists(path)) { File.Replace(tmp, path, null); }
                else { File.Move(tmp, path); }
                return true;
            }
            catch (Exception ex)
            {
                TShockAPI.TShock.Log.Error($"[跑酷] 保存地图失败 {map.Id}: {ex.Message}");
                return false;
            }
        }
    }

    public static bool Delete(string id)
    {
        try
        {
            string p = PathOf(id);
            if (File.Exists(p)) { File.Delete(p); return true; }
        }
        catch { }
        return false;
    }
}
