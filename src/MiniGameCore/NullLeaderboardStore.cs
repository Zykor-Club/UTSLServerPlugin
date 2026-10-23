namespace UTSL.MiniGame;

/// <summary>
/// P1 占位：排行榜落库在 P3 实现（表 + linq2db + 缓存）。
/// 现在只把成绩打到日志里，接口行为与最终一致（空结果/静默成功），方便先跑通流程。
/// </summary>
internal sealed class NullLeaderboardStore : ILeaderboardStore
{
    public Task SubmitAsync(IReadOnlyList<ScoreEntry> entries, CancellationToken ct)
    {
        if (entries.Count > 0)
        {
            TShockAPI.TShock.Log.Info($"[排行榜] P3 未实现，暂时只打日志：{entries.Count} 条（game={entries[0].GameId}, map={entries[0].MapId}, mode={entries[0].Mode}）");
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoreEntry>> TopAsync(string gameId, string mapId, string? mode, string? season,
                                                    int take, bool ascending, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScoreEntry>>(Array.Empty<ScoreEntry>());

    public Task<IReadOnlyList<ScoreEntry>> BestOfAsync(string gameId, string mapId, string playerKey, string? season, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScoreEntry>>(Array.Empty<ScoreEntry>());
}
