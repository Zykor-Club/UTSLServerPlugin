using System;
using UnifierTSL.Servers;

namespace UTSL.VirtualWorld;

/// <summary>
/// 创建/加载一个实例所需的全部信息（全部由玩法决定，核心不替它做策略）。
/// 相对 50 号草案的三处落地修正（已在交接报告中说明）：
///   1) 增加 ServerName —— ServerContext 名字由玩法决定（如 "Pot_xxx"）；
///   2) 增加 LogTag —— 诊断日志前缀（如 "[尘歌壶]"），保证改造前后日志可对比；
///   3) OnLoaded 加一个 bool（本次是否刚用种子新建）+ 新增 OnBeforeEnter；
///      回调改为同步 Action（现有逻辑全在 ServerContext 更新线程上同步改 Main，不需要异步）。
/// </summary>
public sealed class VirtualWorldRequest
{
    public string Key = "";
    public string DisplayName = "";
    public string OwnerKey = "";

    /// <summary>ServerContext 名字；空 = 核心生成 &lt;key 前 12 位&gt;。</summary>
    public string ServerName = "";

    /// <summary>世界文件名（不含目录）。</summary>
    public string WorldFileName = "";

    /// <summary>必须与玩家当前世界一致（跨尺寸有空气墙）。</summary>
    public int TilesX, TilesY;

    /// <summary>种子世界文件；目标世界文件不存在时复制它。</summary>
    public string? SeedWorldPath;

    /// <summary>不建控制台窗口。</summary>
    public bool Headless = true;

    /// <summary>闲置销毁秒数（回收触发由玩法负责，这里只作为元数据）。</summary>
    public int IdleDestroySeconds = 120;

    /// <summary>诊断日志前缀，例如 "[尘歌壶]"。空 = 核心默认前缀。</summary>
    public string LogTag = "";

    /// <summary>玩法策略（可空）。</summary>
    public IVirtualWorldPolicy? Policy;

    /// <summary>加载完成后在【本世界线程】调用一次；bool = 本次是否刚复制了种子（用于"刚播种才清空"）。</summary>
    public Action<ServerContext, bool>? OnLoaded;

    /// <summary>每次进入前在【目标世界线程】调用；bool = 本实例是否由种子新建（用于"每次进入都铺出生平台"）。</summary>
    public Action<ServerContext, bool>? OnBeforeEnter;
}
