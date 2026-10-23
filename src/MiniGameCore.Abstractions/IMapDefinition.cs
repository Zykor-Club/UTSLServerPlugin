namespace UTSL.MiniGame;

/// <summary>
/// 一张地图的元信息。具体几何（起点/终点/存档点/活动区）由玩法自己解释 <see cref="RawJson"/>，
/// 核心只关心：多大、几个坑、多久、用哪个世界模板。
/// </summary>
public interface IMapDefinition
{
    /// <summary>图 id（稳定，进排行榜与房间 key）。</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>世界宽（图格）。必须与主世界一致 —— 受 VirtualWorldCore「跨尺寸有空气墙」限制。</summary>
    int TilesX { get; }

    /// <summary>世界高（图格）。</summary>
    int TilesY { get; }

    /// <summary>一局最多几人。</summary>
    int Capacity { get; }

    /// <summary>单局时限（秒）；0 = 不限。</summary>
    int TimeLimitSeconds { get; }

    /// <summary>首名完赛后的宽限（秒）。</summary>
    int GraceSeconds { get; }

    /// <summary>开局倒计时（秒）；0 = 不要倒计时，进房即开跑。</summary>
    int CountdownSeconds { get; }

    /// <summary>世界模板基线名（交给 VirtualWorldCore 的 TemplateKey）。空 = 不走模板（用种子世界）。</summary>
    string WorldTemplate { get; }
}
