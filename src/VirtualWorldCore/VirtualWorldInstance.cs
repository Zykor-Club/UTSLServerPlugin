using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnifierTSL.Servers;

namespace UTSL.VirtualWorld;

/// <summary>核心内部的实例对象。生命周期字段由服务层写入；玩法状态放 states。</summary>
internal sealed class VirtualWorldInstance : IVirtualWorld
{
    private readonly ConcurrentDictionary<int, byte> occupants = new();
    private readonly ConcurrentDictionary<Type, object> states = new();

    public VirtualWorldInstance(string key, string displayName, string ownerKey)
    {
        Key = key;
        DisplayName = displayName;
        OwnerKey = ownerKey;
        LastActiveUtc = DateTime.UtcNow;
    }

    public string Key { get; }
    public string DisplayName { get; }
    public string OwnerKey { get; }

    public ServerContext? Context { get; internal set; }
    public bool IsLoaded => Context is { IsRunning: true };

    public IReadOnlyCollection<int> Occupants => occupants.Keys.ToArray();
    public DateTime LastActiveUtc { get; internal set; }

    public T GetState<T>() where T : class, new() => (T)states.GetOrAdd(typeof(T), static _ => new T());

    /// <summary>玩法请求（含策略与回调）；Ensure 时会刷新。</summary>
    internal VirtualWorldRequest Request = new();

    /// <summary>同一 key 的并发 Ensure 串行化。</summary>
    internal readonly object LoadGate = new();

    /// <summary>本实例加载时是否刚复制过种子。</summary>
    internal bool WasJustSeeded;

    internal bool AddOccupant(int index)
    {
        LastActiveUtc = DateTime.UtcNow;
        return occupants.TryAdd(index, 0);
    }

    internal bool RemoveOccupant(int index)
    {
        LastActiveUtc = DateTime.UtcNow;
        return occupants.TryRemove(index, out _);
    }

    internal bool HasOccupant(int index) => occupants.ContainsKey(index);
    internal void Touch() => LastActiveUtc = DateTime.UtcNow;
}
