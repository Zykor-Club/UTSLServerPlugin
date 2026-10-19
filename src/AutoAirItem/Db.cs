using System.Text.Json;
using LinqToDB;
using LinqToDB.Mapping;
using TShockAPI;

namespace AutoAirItem;

[Table("AutoTrash")]
public sealed class AutoTrashRow
{
    [PrimaryKey, Identity] public int ID { get; set; }
    [Column, NotNull] public string Name { get; set; } = "";
    [Column] public int Enabled { get; set; }
    [Column] public int Auto { get; set; }
    [Column] public int Mess { get; set; }
    [Column] public string ItemType { get; set; } = "[]";
    [Column] public string DelItem { get; set; } = "{}";
}

public static class AutoTrashDb
{
    public static void Init()
    {
        TShock.DB.CreateTable<AutoTrashRow>(tableOptions: TableOptions.CreateIfNotExists);
    }

    public static List<AutoAirConfig.PlayerData> LoadAll()
    {
        List<AutoAirConfig.PlayerData> list = [];
        foreach (AutoTrashRow row in TShock.DB.GetTable<AutoTrashRow>().ToList())
        {
            list.Add(new AutoAirConfig.PlayerData
            {
                Name = row.Name,
                Enabled = row.Enabled == 1,
                Auto = row.Auto == 1,
                Mess = row.Mess == 1,
                Types = JsonSerializer.Deserialize<List<int>>(row.ItemType) ?? [],
                Deleted = JsonSerializer.Deserialize<Dictionary<int, int>>(row.DelItem) ?? [],
            });
        }
        return list;
    }

    public static void Save(AutoAirConfig.PlayerData data)
    {
        string types = JsonSerializer.Serialize(data.Types);
        string deleted = JsonSerializer.Serialize(data.Deleted);
        int updated = TShock.DB.GetTable<AutoTrashRow>()
            .Where(x => x.Name == data.Name)
            .Set(x => x.Enabled, data.Enabled ? 1 : 0)
            .Set(x => x.Auto, data.Auto ? 1 : 0)
            .Set(x => x.Mess, data.Mess ? 1 : 0)
            .Set(x => x.ItemType, types)
            .Set(x => x.DelItem, deleted)
            .Update();
        if (updated == 0)
        {
            TShock.DB.Insert(new AutoTrashRow
            {
                Name = data.Name,
                Enabled = data.Enabled ? 1 : 0,
                Auto = data.Auto ? 1 : 0,
                Mess = data.Mess ? 1 : 0,
                ItemType = types,
                DelItem = deleted,
            });
        }
    }

    public static void Clear() => TShock.DB.GetTable<AutoTrashRow>().Delete();
}
