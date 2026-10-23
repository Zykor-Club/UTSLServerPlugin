namespace UTSL.MiniGame;

/// <summary>
/// 小游戏契约版本。接口只增不改；破坏性变更必须提升此值（玩法发现时校验，和 VirtualWorldApi 同一套做法）。
/// v1：初版 —— 房间状态机 / 地图定义 / 大厅与排队 / 排行榜存储。
/// </summary>
public static class MiniGameApi
{
    public const int Version = 1;
}
