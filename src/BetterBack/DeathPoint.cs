using System;
using System.Text.Json.Serialization;

namespace BetterBack;

public sealed class DeathPoint
{
    [JsonPropertyName("名称")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("X坐标")] public int X { get; set; }
    [JsonPropertyName("Y坐标")] public int Y { get; set; }
    [JsonPropertyName("死亡时间")] public DateTime DeathTime { get; set; }
    [JsonPropertyName("死亡原因")] public string DeathReason { get; set; } = "未知原因";
    [JsonPropertyName("世界ID")] public string WorldID { get; set; } = string.Empty;

    public DeathPoint() { }

    public DeathPoint(string name, int x, int y, string reason, string worldId)
    {
        Name = name;
        X = x;
        Y = y;
        DeathTime = DateTime.Now;
        DeathReason = reason;
        WorldID = worldId;
    }

    public override string ToString() => 
        $"{Name} ({X / 16}, {Y / 16}) - {DeathTime:MM-dd HH:mm}";

    public double DistanceTo(int tileX, int tileY) => 
        Math.Sqrt(Math.Pow(X / 16 - tileX, 2) + Math.Pow(Y / 16 - tileY, 2));
}
