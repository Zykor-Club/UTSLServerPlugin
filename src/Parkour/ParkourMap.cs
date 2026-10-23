using System.Text.Json;
using System.Text.Json.Serialization;
using UTSL.MiniGame;

namespace Parkour;

/// <summary>图格矩形（含边界）。</summary>
public readonly record struct TileRect(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0 + 1;
    public int Height => Y1 - Y0 + 1;

    public bool Contains(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;

    /// <summary>带 Y 容差的判定（平台跳跃时脚下一两格不该漏）。</summary>
    public bool ContainsWithTolerance(int x, int y, int tolerance)
        => x >= X0 && x <= X1 && y >= Y0 - tolerance && y <= Y1 + tolerance;

    /// <summary>以 (cx,cy) 为心、半径 r 的正方形区域。</summary>
    public static TileRect Around(int cx, int cy, int r) => new(cx - r, cy - r, cx + r, cy + r);

    public override string ToString() => $"({X0},{Y0})-({X1},{Y1})";
}

/// <summary>存档点：矩形 + 重生坐标（存 JSON）。</summary>
public sealed class SavePoint
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("x0")] public int X0 { get; set; }
    [JsonPropertyName("y0")] public int Y0 { get; set; }
    [JsonPropertyName("x1")] public int X1 { get; set; }
    [JsonPropertyName("y1")] public int Y1 { get; set; }
    [JsonPropertyName("spawnX")] public int SpawnX { get; set; }
    [JsonPropertyName("spawnY")] public int SpawnY { get; set; }

    [JsonIgnore] public TileRect Rect => new(X0, Y0, X1, Y1);
}

/// <summary>
/// 跑酷地图（JSON 落盘：config/Parkour/maps/&lt;id&gt;.json）。
/// 几何全部是图格整数矩形；终点不要求集齐存档点（抄近路算技巧）。
/// </summary>
public sealed class ParkourMap : IMapDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("tilesX")] public int TilesX { get; set; }
    [JsonPropertyName("tilesY")] public int TilesY { get; set; }
    [JsonPropertyName("capacity")] public int Capacity { get; set; } = 1;
    [JsonPropertyName("timeLimitSeconds")] public int TimeLimitSeconds { get; set; } = 900;
    [JsonPropertyName("graceSeconds")] public int GraceSeconds { get; set; } = 60;
    /// <summary>开局倒计时（秒）；倒计时期间玩家被锁在起点区域内。</summary>
    [JsonPropertyName("countdownSeconds")] public int CountdownSeconds { get; set; } = 3;
    [JsonPropertyName("worldTemplate")] public string WorldTemplate { get; set; } = "";
    /// <summary>该图使用的世界文件（Worlds\ 下，不含 .wld）；空 = 用空白世界。</summary>
    [JsonPropertyName("worldFile")] public string WorldFile { get; set; } = "";


    // ===== 区域（作者在建造世界里圈出来）=====
    [JsonPropertyName("activity")] public int[] Activity { get; set; } = Array.Empty<int>();   // [x0,y0,x1,y1]
    [JsonPropertyName("start")] public int[] Start { get; set; } = Array.Empty<int>();
    [JsonPropertyName("startSpawn")] public int[] StartSpawn { get; set; } = Array.Empty<int>(); // [x,y]
    [JsonPropertyName("finish")] public int[] Finish { get; set; } = Array.Empty<int>();
    [JsonPropertyName("saves")] public List<SavePoint> Saves { get; set; } = new();

    // ===== 计分 / 判定参数 =====
    [JsonPropertyName("yTolerance")] public int YTolerance { get; set; } = 6;
    [JsonPropertyName("scoreBase")] public int ScoreBase { get; set; } = 1000;
    [JsonPropertyName("scorePerSecond")] public int ScorePerSecond { get; set; } = 5;
    [JsonPropertyName("scorePerSave")] public int ScorePerSave { get; set; } = 20;

    [JsonIgnore] public bool HasStart => Start.Length == 4;
    [JsonIgnore] public bool HasFinish => Finish.Length == 4;
    [JsonIgnore] public TileRect ActivityRect => ToRect(Activity);
    [JsonIgnore] public TileRect StartRect => ToRect(Start);
    [JsonIgnore] public TileRect FinishRect => ToRect(Finish);

    public static TileRect ToRect(int[] a) => a.Length == 4 ? new TileRect(a[0], a[1], a[2], a[3]) : default;
    public static int[] FromRect(TileRect r) => new[] { r.X0, r.Y0, r.X1, r.Y1 };

    /// <summary>建图时只放一个"空图"，区域全部由作者圈。</summary>
    public static ParkourMap CreateBlank(string id, string name, int tilesX, int tilesY) => new()
    {
        Id = id,
        DisplayName = name,
        TilesX = tilesX,
        TilesY = tilesY,
        Capacity = 1,
        TimeLimitSeconds = 900,
        GraceSeconds = 60,
        CountdownSeconds = 3,
        WorldTemplate = "",
    };

    /// <summary>
    /// 校验：必须有活动区，且起点/终点/全部存档点都**在活动区内**。
    /// 返回空列表 = 合法。
    /// </summary>
    public List<string> Validate()
    {
        List<string> problems = new();
        if (Activity.Length != 4) { problems.Add("活动区未设置（先 /pk here activity）"); return problems; }

        TileRect act = ActivityRect;
        if (!HasStart) { problems.Add("起点未设置（/pk here start）"); }
        else if (!Inside(act, StartRect)) { problems.Add($"起点 {StartRect} 超出活动区 {act}"); }
        else if (StartRect.Width < 3 || StartRect.Height < 3) { problems.Add($"起点太小（{StartRect.Width}x{StartRect.Height}），至少 3x3"); }
        else if (StartRect.X0 < 41 || StartRect.Y0 < 41) { problems.Add($"起点太靠世界边界（Teleport 会夹到 40 格外）：{StartRect}"); }

        if (!HasFinish) { problems.Add("终点未设置（/pk here finish）"); }
        else if (!Inside(act, FinishRect)) { problems.Add($"终点 {FinishRect} 超出活动区 {act}"); }
        else if (FinishRect.Width < 3 || FinishRect.Height < 3) { problems.Add($"终点太小（{FinishRect.Width}x{FinishRect.Height}），至少 3x3"); }

        foreach (SavePoint sp in Saves)
        {
            if (!Inside(act, sp.Rect)) { problems.Add($"存档点 {sp.Index} {sp.Rect} 超出活动区 {act}"); }
        }
        return problems;
    }

    /// <summary>inner 是否完全包含在 outer 内。</summary>
    public static bool Inside(TileRect outer, TileRect inner)
        => inner.X0 >= outer.X0 && inner.X1 <= outer.X1 && inner.Y0 >= outer.Y0 && inner.Y1 <= outer.Y1;

    public string ToJson() => JsonSerializer.Serialize(this, ParkourMapStore.JsonOptions);

    public static ParkourMap? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<ParkourMap>(json, ParkourMapStore.JsonOptions); }
        catch { return null; }
    }

    public override string ToString() => $"{Id}·{DisplayName}·{(TilesX > 0 ? $"{TilesX}x{TilesY}" : "跟随当前世界")}";
}
