using System;
using System.Collections.Generic;
using UnifierTSL.Servers;

namespace UTSL.VirtualWorld;

/// <summary>一个假世界实例。核心只解释生命周期与占用者；玩法自带状态放 GetState。</summary>
public interface IVirtualWorld
{
    /// <summary>稳定标识（如 acct:&lt;uuid&gt; / room:123 / arena:boss_eye）。</summary>
    string Key { get; }

    /// <summary>世界名（"&lt;名字&gt;的尘歌壶" / "PvP 房间A"）。</summary>
    string DisplayName { get; }

    /// <summary>拥有者标识；空串 = 公共实例。</summary>
    string OwnerKey { get; }

    /// <summary>已加载时非空。</summary>
    ServerContext? Context { get; }

    bool IsLoaded { get; }

    /// <summary>当前在里面的玩家索引。</summary>
    IReadOnlyCollection<int> Occupants { get; }

    DateTime LastActiveUtc { get; }

    /// <summary>玩法自定义的每实例状态（核心不解释其内容）。</summary>
    T GetState<T>() where T : class, new();
}
