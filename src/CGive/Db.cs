using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using TShockAPI;

namespace CGive;

[Table("CGive")]
public sealed class CgiveRow
{
    [PrimaryKey, Identity] public int ID { get; set; }
    [Column] public string executer { get; set; } = "server";
    [Column] public string cmd { get; set; } = "";
    [Column] public string who { get; set; } = "-1";
}

[Table("Given")]
public sealed class GivenRow
{
    [Column] public string Name { get; set; } = "";
    [Column] public int Id { get; set; }
}

public static class CGiveDb
{
    public static void Init()
    {
        TShock.DB.CreateTable<CgiveRow>(tableOptions: TableOptions.CreateIfNotExists);
        // Given 没有主键，LinqToDB 的 CreateTable 可能跳过，显式建表兜底
        TShock.DB.Execute("CREATE TABLE IF NOT EXISTS Given (Name TEXT NOT NULL, Id INTEGER NOT NULL)");
    }

    public static int Add(string executer, string cmd, string who)
    {
        object id = TShock.DB.InsertWithIdentity(new CgiveRow { executer = executer, cmd = cmd, who = who });
        return Convert.ToInt32(id);
    }

    public static List<CgiveRow> All() => TShock.DB.GetTable<CgiveRow>().OrderBy(x => x.ID).ToList();

    public static List<CgiveRow> ForPlayer(string name) =>
        TShock.DB.GetTable<CgiveRow>().Where(x => x.who == name || x.who == "-1").ToList();

    public static List<CgiveRow> ForWho(string name) =>
        TShock.DB.GetTable<CgiveRow>().Where(x => x.who == name).ToList();

    public static void Delete(int id)
    {
        TShock.DB.GetTable<CgiveRow>().Where(x => x.ID == id).Delete();
        TShock.DB.GetTable<GivenRow>().Where(x => x.Id == id).Delete();
    }

    public static void Clear()
    {
        TShock.DB.GetTable<CgiveRow>().Delete();
        TShock.DB.GetTable<GivenRow>().Delete();
    }

    public static bool IsGiven(string name, int id) =>
        TShock.DB.GetTable<GivenRow>().Any(x => x.Name == name && x.Id == id);

    public static void AddGiven(string name, int id)
    {
        TShock.DB.Insert(new GivenRow { Name = name, Id = id });
    }
}
