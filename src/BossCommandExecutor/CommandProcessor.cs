using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Terraria;
using TShockAPI;
using UnifierTSL.Servers;

namespace BossCommandExecutor
{
    /// <summary>
    /// 命令执行与占位符处理服务
    /// </summary>
    public class CommandProcessor
    {
        // 占位符正则：匹配 {name} 或 {name.sub} 格式
        private static readonly Regex PlaceholderRegex = new(@"\{([a-zA-Z0-9_.]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// 异步执行命令
        /// </summary>
        public async Task<bool> ExecuteAsync(
            ServerContext server,
            string rawCommand, 
            Configuration.BossCommandConfig config, 
            NPC npc)
        {
            if (string.IsNullOrWhiteSpace(rawCommand)) return false;

            try
            {
                // 替换占位符
                string processedCmd = ReplacePlaceholders(server, rawCommand, config, npc);
                
                // UTSL: TShock 的 Commands.HandleCommand 第一个参数是 CommandExecutor 而不是 TSPlayer。
                // byte.MaxValue = 控制台执行者，等价于上游的 TSPlayer.Server。
                Commands.HandleCommand(new CommandExecutor(server, byte.MaxValue), processedCmd);
                
                // 控制台提示
                if (BossCommandPlugin.Config.ConsoleSuccessPrompt)
                {
                    TShock.Log.Info($"[BossCommand] ✓ 执行成功: {processedCmd}");
                }
                else
                {
                    TShock.Log.Debug($"[BossCommand] 执行: {processedCmd}");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"[BossCommand] 执行失败 '{rawCommand}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 替换命令中的占位符
        /// 支持：{boss}, {boss.name}, {boss.id}, {time}, {date}, {count}, {exec_count}
        /// </summary>
        private string ReplacePlaceholders(ServerContext server, string input, Configuration.BossCommandConfig config, NPC npc)
        {
            string result = input;
            var now = DateTime.Now;

            // 使用字典映射进行批量替换
            var replacements = new System.Collections.Generic.Dictionary<string, string>
            {
                ["{boss}"] = config.Name,
                ["{boss.name}"] = config.Name,
                ["{boss.id}"] = npc.netID.ToString(),
                ["{boss.type}"] = npc.type.ToString(),
                ["{time}"] = now.ToString("HH:mm:ss"),
                ["{date}"] = now.ToString("yyyy-MM-dd"),
                ["{count}"] = config.ExecutionCount.ToString(),
                ["{exec_count}"] = config.ExecutionCount.ToString(),
                // UTSL 多世界：不能读全局 Main.worldName
                ["{world}"] = server.Main.worldName,
                ["{players}"] = TShockAPI.Utils.GetActivePlayerCount().ToString()
            };

            foreach (var (key, value) in replacements)
            {
                result = result.Replace(key, value, StringComparison.OrdinalIgnoreCase);
            }

            return result;
        }
    }
}
