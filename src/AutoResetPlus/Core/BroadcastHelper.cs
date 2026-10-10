using TShockAPI;

namespace AutoResetPlus.Core;

/// <summary>向所有世界的玩家广播（UTSL 没有 TSPlayer.All，逐个发）。</summary>
internal static class BroadcastHelper
{
    public static void Broadcast(string msg, Microsoft.Xna.Framework.Color color)
    {
        try
        {
            foreach (TShockAPI.TSPlayer p in TShockAPI.TShock.Players)
            {
                p?.SendMessage(msg, color);
            }
        }
        catch
        {
        }
        TShockAPI.TShock.Log.Info("[广播] " + msg);
    }
}
