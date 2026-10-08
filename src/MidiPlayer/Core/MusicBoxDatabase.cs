using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using TShockAPI;

namespace MidiPlayer.Core;

/// <summary>
/// 八音盒绑定数据库访问层 —— 以世界唯一 ID 为键，存/取/删绑定坐标。
/// 存入 tshock.sqlite，切换世界后按 WorldId 自动恢复对应绑定。
///
/// ⚠️ UTSL 差异：上游用 TShock 的 Query/QueryReader，UTSL 已迁到 linq2db，
/// 这里改用 DataConnection + CreateTable（与 TShockAPI 自身一致）。
/// 表结构保持不变（WorldId 主键 / X / Y），老数据兼容。
/// </summary>
public static class MusicBoxDatabase
{
    private const string TableName = "MidiPlayerMusicBox";

    private static readonly object Lock = new();

    [Table(Name = TableName)]
    private sealed class MusicBoxRecord
    {
        [Column, PrimaryKey, NotNull] public int WorldId { get; set; }
        [Column, NotNull] public int X { get; set; }
        [Column, NotNull] public int Y { get; set; }
    }

    /// <summary>linq2db 的 DataConnection 非线程安全，每次操作单独开一个</summary>
    private static DataConnection Open() => new(TShock.DB.Options);

    /// <summary>初始化数据库表（不存在则创建）。</summary>
    public static void Initialize()
    {
        lock (Lock)
        {
            using DataConnection db = Open();
            db.CreateTable<MusicBoxRecord>(tableOptions: TableOptions.CreateIfNotExists);
        }
    }

    /// <summary>查询指定世界的八音盒绑定坐标（×2 左上角）。无绑定返回 null。</summary>
    public static PointBinding? GetBinding(int worldId)
    {
        lock (Lock)
        {
            using DataConnection db = Open();
            MusicBoxRecord? row = db.GetTable<MusicBoxRecord>().FirstOrDefault(r => r.WorldId == worldId);
            return row is null ? null : new PointBinding(row.X, row.Y);
        }
    }

    /// <summary>保存（或覆盖）指定世界的八音盒绑定坐标。</summary>
    public static void SaveBinding(int worldId, int x, int y)
    {
        lock (Lock)
        {
            using DataConnection db = Open();
            ITable<MusicBoxRecord> table = db.GetTable<MusicBoxRecord>();

            int affected = table
                .Where(r => r.WorldId == worldId)
                .Set(r => r.X, x)
                .Set(r => r.Y, y)
                .Update();

            if (affected == 0)
            {
                try
                {
                    table.Insert(() => new MusicBoxRecord { WorldId = worldId, X = x, Y = y });
                }
                catch
                {
                    // 并发下可能已被插入，退化为更新
                    table.Where(r => r.WorldId == worldId).Set(r => r.X, x).Set(r => r.Y, y).Update();
                }
            }
        }
    }

    /// <summary>删除指定世界的八音盒绑定。返回是否删除成功。</summary>
    public static bool DeleteBinding(int worldId)
    {
        lock (Lock)
        {
            using DataConnection db = Open();
            return db.GetTable<MusicBoxRecord>().Where(r => r.WorldId == worldId).Delete() > 0;
        }
    }
}
