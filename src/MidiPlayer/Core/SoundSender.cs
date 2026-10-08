using Microsoft.Xna.Framework;
using MidiPlayer.Models;
using Terraria;
using Terraria.ID;
using TShockAPI;
using UnifierTSL.Extensions;
using UnifierTSL.Servers;

namespace MidiPlayer.Core;

public class SoundSender
{
    // 从 SoundID.IndexByName 初始化乐器 soundIndex 映射
    /// <summary>
    /// 初始化乐器 soundIndex 映射。
    ///
    /// ⚠️⚠️ 关键：**不能** 从 <c>SoundID.IndexByName</c> 取！必须固定用物品 ID（26/35/47/60）。
    ///
    /// 原因（实测踩坑，会导致客户端崩溃）：
    ///   - MidiPlayer 是"服务端发音符包 + 客户端插件(R.MidiSoundEngine)用 NAudio 旁路播放"的双端协议，
    ///     而客户端插件是按音符包的 <c>sound</c> 字段值（26/35/47/60，即**物品 ID**）来识别乐器的
    ///     （它按此加载 Item_26.xnb / Item_35.xnb / Item_47.xnb）。
    ///   - TShock 下 SoundID.IndexByName 由 Terraria 自己填充，"Item26" -> 26，所以上游一直发的是物品 ID。
    ///   - UTSL 下该字典为 null；调用 SoundID.FillAccessMap() 会按 **SoundID 枚举值**填充
    ///     （"Item26" -> 178），协议值一变，客户端插件就认不出这些音符 →
    ///     不走它的 NAudio 旁路 → 回落到游戏原生音效引擎 →
    ///     密集段落音效实例无限堆积 → 客户端越来越卡直至崩溃。
    ///
    /// 结论：固定使用物品 ID，与 TShock 行为保持二进制一致。
    /// </summary>
    public static void Initialize()
    {
        foreach (InstrumentType instrument in Enum.GetValues<InstrumentType>())
        {
            if (InstrumentSoundIndex.ContainsKey(instrument)) continue;
            string soundName = GetSoundName(instrument);
            if (SoundID.IndexByName != null && SoundID.IndexByName.TryGetValue(soundName, out ushort index))
            {
                InstrumentSoundIndex[instrument] = index;
            }
            else
            {
                ushort fallback = instrument switch
                {
                    InstrumentType.Harp => 26,
                    InstrumentType.Bell => 35,
                    InstrumentType.GuitarAxe => 47,
                    InstrumentType.Guitar => 47,
                    InstrumentType.Drum => 60,
                    _ => 26
                };
                InstrumentSoundIndex[instrument] = fallback;
            }
        }

        // 把解析出来的索引打进正式日志（原来用 Console.WriteLine，落不到日志里）
        TShock.Log.Info("[MidiPlayer] 乐器SoundIndex映射: " +
            string.Join(", ", InstrumentSoundIndex.Select(kv => kv.Key + "=" + kv.Value)));
        TShock.Log.Info("[MidiPlayer] SoundID.IndexByName 原始值: " +
            $"Item26={(SoundID.IndexByName != null && SoundID.IndexByName.TryGetValue("Item26", out ushort v26) ? v26 : -1)} " +
            $"Item35={(SoundID.IndexByName != null && SoundID.IndexByName.TryGetValue("Item35", out ushort v35) ? v35 : -1)} " +
            $"Item47={(SoundID.IndexByName != null && SoundID.IndexByName.TryGetValue("Item47", out ushort v47) ? v47 : -1)} " +
            $"Item60={(SoundID.IndexByName != null && SoundID.IndexByName.TryGetValue("Item60", out ushort v60) ? v60 : -1)} " +
            $"(字典大小={(SoundID.IndexByName?.Count ?? -1)})");
    }

    private static string GetSoundName(InstrumentType instrument) => instrument switch
    {
        InstrumentType.Harp => "Item26",
        InstrumentType.Bell => "Item35",
        InstrumentType.GuitarAxe => "Item47",
        InstrumentType.Guitar => "Item47",
        InstrumentType.Drum => "Item60",
        _ => "Item26"
    };

    private static readonly Dictionary<InstrumentType, ushort> InstrumentSoundIndex = new();

    private readonly object _lock = new();
    private readonly HashSet<int> _failedClients = new();
    private readonly HashSet<int> _silentPlayers = new();
    private readonly Random _rng = new();
    private readonly Dictionary<int, Dictionary<int, StyleSendState>> _lastStyleSent = new();

    public int SameStyleRetriggerMs { get; set; } = 35;

    public void SendNote(int playerIndex, TerrariaNote note, Vector2 position, float volumeMultiplier)
    {
        lock (_lock)
        {
            if (_failedClients.Contains(playerIndex))
                return;

            try
            {
                // UTSL：Main.player 静态不存在，玩家要经 TSPlayer 取到所属世界
                TSPlayer? tsPlayer = TShock.Players[playerIndex];
                ServerContext? server = tsPlayer?.GetCurrentServer();
                if (server is null)
                {
                    _failedClients.Add(playerIndex);
                    return;
                }

                var player = server.Main.player[playerIndex];
                if (player == null || !player.active)
                {
                    _failedClients.Add(playerIndex);
                    return;
                }

                ushort soundIndex = InstrumentSoundIndex.TryGetValue(note.Instrument, out ushort idx) ? idx : (ushort)26;

                int soundStyle = note.Instrument switch
                {
                    InstrumentType.Drum or InstrumentType.Guitar => note.SoundStyle,
                    _ => note.SoundStyle
                };

                float pitchOffset = note.PitchOffset;
                if (pitchOffset == -1f)
                    pitchOffset = -0.99f;
                pitchOffset = Math.Clamp(pitchOffset, -0.99f, 1f);

                float sendVolume = Math.Clamp(note.Volume * volumeMultiplier, 0f, 1f);

                // Guitar chords and drums only keep one client instance per style.
                if (IsNonOverlapStyle(soundStyle) && !CanRetrigger(playerIndex, soundStyle, sendVolume))
                    return;

                // Small deterministic spatial offset for panning variety.
                // Client instance truncation is handled by the retrigger gate above.
                float microAngle = note.OriginalNoteNumber * 2.828f;
                float microRadius = 8f;
                Vector2 microOffset = new(
                    microRadius * MathF.Cos(microAngle),
                    microRadius * MathF.Sin(microAngle));

                // 额外 ±1 随机防抖：防止同音高+同位置+同时刻的两个音符完全重合
                microOffset.X += _rng.Next(-1, 2);
                microOffset.Y += _rng.Next(-1, 2);

                var soundInfo = new NetMessage.NetSoundInfo(
                    position: position + microOffset,
                    soundIndex: soundIndex,
                    style: soundStyle,
                    volume: sendVolume,
                    pitchOffset: pitchOffset
                );

                server.NetMessage.PlayNetSound(soundInfo, remoteClient: playerIndex);

                if (IsNonOverlapStyle(soundStyle))
                    RecordSent(playerIndex, soundStyle, sendVolume);
            }
            catch
            {
                _failedClients.Add(playerIndex);
            }
        }
    }

    public void Reset(int playerIndex)
    {
        lock (_lock)
        {
            _failedClients.Remove(playerIndex);
            _lastStyleSent.Remove(playerIndex);
        }
    }

    // 重置所有玩家发声状态，不清除静默状态
    public void ResetAll()
    {
        lock (_lock)
        {
            _failedClients.Clear();
            _lastStyleSent.Clear();
        }
    }

    public void SetSilent(int playerIndex, bool silent)
    {
        lock (_lock)
        {
            if (silent)
                _silentPlayers.Add(playerIndex);
            else
                _silentPlayers.Remove(playerIndex);
        }
    }

    public bool IsSilent(int playerIndex)
    {
        lock (_lock) return _silentPlayers.Contains(playerIndex);
    }

    public void ClearSilent(int playerIndex)
    {
        lock (_lock) _silentPlayers.Remove(playerIndex);
    }

    private static bool IsNonOverlapStyle(int soundStyle)
    {
        return soundStyle is >= 133 and <= 148;
    }

    private bool CanRetrigger(int playerIndex, int soundStyle, float sendVolume)
    {
        if (!_lastStyleSent.TryGetValue(playerIndex, out var styles))
            return true;
        if (!styles.TryGetValue(soundStyle, out var last))
            return true;

        long delta = Environment.TickCount64 - last.SentMs;
        return delta >= SameStyleRetriggerMs || sendVolume > last.Volume * 1.15f;
    }

    private void RecordSent(int playerIndex, int soundStyle, float sendVolume)
    {
        if (!_lastStyleSent.TryGetValue(playerIndex, out var styles))
            _lastStyleSent[playerIndex] = styles = new Dictionary<int, StyleSendState>();

        styles[soundStyle] = new StyleSendState
        {
            SentMs = Environment.TickCount64,
            Volume = sendVolume
        };
    }

    private sealed class StyleSendState
    {
        public long SentMs;
        public float Volume;
    }
}
