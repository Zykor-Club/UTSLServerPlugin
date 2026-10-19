using System.Data;
using System.Diagnostics;
using System.Text;
using TShockAPI;
using UnifierTSL.Plugins;

namespace ConsoleSql;

/// <summary>控制台 SQL 执行器（UTSL 版：BasePlugin + PluginMetadata）。</summary>
[PluginMetadata("ConsoleSql", "1.0.4", "Cai", "让控制台可以执行SQL操作数据库")]
public sealed class ConsoleSqlPlugin : BasePlugin
{
    public override int InitializationOrder => TShock.Order + 1;

    public override async Task InitializeAsync(
        IPluginConfigRegistrar configRegistrar,
        System.Collections.Immutable.ImmutableArray<PluginInitInfo> priorInitializations,
        CancellationToken cancellationToken = default)
    {
        foreach (PluginInitInfo initInfo in priorInitializations)
        {
            if (initInfo.Plugin.Name == "TShock") { await initInfo.InitializationTask; }
        }

        Commands.ChatCommands.Add(new Command("consolesql.use", Cmd, "sql"));
        TShock.Log.Info("[ConsoleSql] 已加载");
    }

    private static void Cmd(CommandArgs args)
    {
        if (args.Player is not null && args.Player.RealPlayer) { Reply(args, "此命令仅支持控制台执行。", true); return; }

        if (args.Parameters.Count == 0)
        {
            Reply(args, "格式：sql <SQL 语句>", true);
            Reply(args, "常用：SELECT name FROM sqlite_master WHERE type='table'; / SHOW TABLES;", false);
            Reply(args, "      SELECT * FROM <表> LIMIT 10; / DELETE FROM <表> WHERE <条件>;", false);
            return;
        }

        string sql = string.Join(" ", args.Parameters);
        Reply(args, "> " + sql, false);
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            System.Data.Common.DbConnection conn = TShock.DB.Connection;
            bool opened = false;
            if (conn.State != System.Data.ConnectionState.Open) { conn.Open(); opened = true; }
            try
            {
            using System.Data.Common.DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using System.Data.Common.DbDataReader reader = cmd.ExecuteReader();
            DataTable dt = new();
            dt.Load(reader);
            sw.Stop();

            if (dt.Columns.Count == 0)
            {
                Reply(args, $"执行成功，影响 {reader.RecordsAffected} 行（{sw.Elapsed.TotalSeconds:F2}s）", false);
                return;
            }

            StringBuilder sb = new();
            foreach (DataColumn col in dt.Columns) { sb.AppendFormat("| {0,-24} ", col.ColumnName); }
            sb.AppendLine();
            foreach (DataRow row in dt.Rows)
            {
                foreach (DataColumn col in dt.Columns) { sb.AppendFormat("| {0,-24} ", row[col]); }
                sb.AppendLine();
            }
            sb.Append($"（{dt.Rows.Count} 行，{sw.Elapsed.TotalSeconds:F2}s）");
            Reply(args, sb.ToString(), false);
            }
            finally
            {
                if (opened) { conn.Close(); }
            }
        }
        catch (Exception ex)
        {
            Reply(args, "SQL 执行失败：" + ex.Message, true);
        }
    }

    private static void Reply(CommandArgs args, string text, bool error)
    {
        if (args.Player is not null && args.Player.RealPlayer)
        {
            if (error) { args.Player.SendErrorMessage(text); } else { args.Player.SendInfoMessage(text); }
        }
        else
        {
            if (error) { TShock.Log.Warning(text); } else { TShock.Log.Info(text); }
        }
    }
}
