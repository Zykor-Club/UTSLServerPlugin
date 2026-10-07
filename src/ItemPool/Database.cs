using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using TShockAPI;

namespace ItemPool;

/// <summary>
/// 数据库访问层 — 管理玩家领取记录
///
/// UTSL 版改动：上游 TShock 的 <c>TShock.DB.Query()</c> / <c>QueryReader()</c> 在 UTSL 的 TShockAPI
/// 移植版里已被移除（整个 DB 层换成了 linq2db），所以这里改用 linq2db 的表映射写法。
/// 表结构与原版完全一致，老数据库文件可以直接继续用。
/// </summary>
public static class ItemPoolDatabase
{
    [Table(Name = "ItemPool")]
    private sealed class ItemPoolRecord
    {
        [Column(DataType = DataType.VarChar, Length = 50), PrimaryKey, NotNull]
        public string PlayerName { get; set; } = "";

        [Column(DataType = DataType.VarChar, Length = 50), PrimaryKey, NotNull]
        public string PoolName { get; set; } = "";

        [Column, PrimaryKey, NotNull]
        public int ItemId { get; set; }

        [Column, NotNull]
        public int PickCount { get; set; }
    }

    private static readonly object _lock = new();

    /// <summary>
    /// 借 TShock 已建立的连接参数新建一个连接。
    /// linq2db 的 DataConnection 不是线程安全的，所以每次操作单独开一个（与 TShockAPI 自身的写法一致）。
    /// </summary>
    private static DataConnection Open() => new(TShock.DB.Options);

    /// <summary>
    /// 初始化数据库表
    /// </summary>
    public static void Initialize()
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            db.CreateTable<ItemPoolRecord>(tableOptions: TableOptions.CreateIfNotExists);
        }
    }

    /// <summary>
    /// 检查玩家是否已领取某池中的某个物品（主要用于按物品模式）
    /// </summary>
    public static bool HasPicked(string playerName, string poolName, int itemId)
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            return db.GetTable<ItemPoolRecord>().Any(r =>
                r.PlayerName == playerName && r.PoolName == poolName && r.ItemId == itemId);
        }
    }

    /// <summary>
    /// 记录玩家领取。按物品模式：PickCount=1；按次数模式：在 ItemId=0 记录上递增
    /// </summary>
    public static void RecordPick(string playerName, string poolName, int itemId)
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            ITable<ItemPoolRecord> table = db.GetTable<ItemPoolRecord>();

            // 先尝试更新已存在的记录
            int affected = table
                .Where(r => r.PlayerName == playerName && r.PoolName == poolName && r.ItemId == itemId)
                .Set(r => r.PickCount, r => r.PickCount + 1)
                .Update();

            // 没有更新到任何行，说明记录不存在，插入新行
            if (affected == 0)
            {
                table.Insert(() => new ItemPoolRecord
                {
                    PlayerName = playerName,
                    PoolName = poolName,
                    ItemId = itemId,
                    PickCount = 1
                });
            }
        }
    }

    /// <summary>
    /// 获取玩家在按次数模式下的已领取总次数
    /// </summary>
    public static int GetPickCount(string playerName, string poolName)
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            return db.GetTable<ItemPoolRecord>()
                .Where(r => r.PlayerName == playerName && r.PoolName == poolName && r.ItemId == 0)
                .Select(r => r.PickCount)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// 获取玩家在指定池中已领取的物品ID列表（按物品模式用）
    /// </summary>
    public static HashSet<int> GetPickedItems(string playerName, string poolName)
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            return db.GetTable<ItemPoolRecord>()
                .Where(r => r.PlayerName == playerName && r.PoolName == poolName && r.ItemId != 0)
                .Select(r => r.ItemId)
                .ToHashSet();
        }
    }

    /// <summary>
    /// 重置指定玩家的领取记录（不指定池名则重置全部）
    /// </summary>
    public static int ResetPlayer(string playerName, string? poolName = null)
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            IQueryable<ItemPoolRecord> query = db.GetTable<ItemPoolRecord>()
                .Where(r => r.PlayerName == playerName);

            if (!string.IsNullOrEmpty(poolName))
            {
                query = query.Where(r => r.PoolName == poolName);
            }

            return query.Delete();
        }
    }

    /// <summary>
    /// 重置所有玩家的领取记录（不指定池名则清空整个表）
    /// </summary>
    public static int ResetAll(string? poolName = null)
    {
        lock (_lock)
        {
            using DataConnection db = Open();
            IQueryable<ItemPoolRecord> query = db.GetTable<ItemPoolRecord>();

            if (!string.IsNullOrEmpty(poolName))
            {
                query = query.Where(r => r.PoolName == poolName);
            }

            return query.Delete();
        }
    }
}
