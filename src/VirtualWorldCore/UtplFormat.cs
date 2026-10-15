using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Terraria;
using Terraria.DataStructures;

namespace UTSL.VirtualWorld;

/// <summary>
/// 一个矩形地区的图格快照（.utpl 的内存形态）。坐标全部是【世界坐标】。
/// </summary>
internal sealed class UtplRegion
{
    public int X, Y, W, H;
    public TileData[] Tiles = [];          // 长度 = W*H，索引 = (y-Y)*W + (x-X)（行优先）
    public List<UtplChest> Chests = [];
    public List<UtplSign> Signs = [];
    public List<UtplEntity> Entities = [];  // 物品框/盘子/人偶/帽架/逻辑感应器/武器架

    public TileData At(int lx, int ly) => Tiles[ly * W + lx];
}

/// <summary>箱子记录：x,y 是【模板内相对坐标】，物品 40 格。</summary>
internal sealed class UtplChest
{
    public int X, Y;
    public string Name = "";
    /// <summary>40 格物品；type=0 表示空格。</summary>
    public (int type, int stack, int prefix)[] Items = new (int, int, int)[40];
}

/// <summary>标牌记录：x,y 是【模板内相对坐标】，text 是牌面文字（原样保存）。</summary>
internal sealed class UtplSign
{
    public int X, Y;
    public string Text = "";
}

/// <summary>实体记录（按 kind 区分）。</summary>
internal sealed class UtplEntity
{
    /// <summary>见 <see cref="UtplFormat.Kind*"/>。</summary>
    public byte Kind;
    public int X, Y;
    /// <summary>槽位物品（物品框/盘子/人偶/帽架各槽）。</summary>
    public List<(int type, int stack, int prefix)> Slots = [];
    /// <summary>逻辑感应器的 on 状态。</summary>
    public bool On;
}

/// <summary>
/// .utpl —— 区块建筑模板的【中立】二进制格式（GZip 压缩）。
///
/// ★ 设计原则（v3 起）：**只存语义字段，绝不存 OTAPI 的位域**。
///   原因：本格式有两端 —— 服务端（OTAPI TileData）与管理器（TEdit.Terraria Tile）。
///   TEdit 的 Tile 是语义化 struct（IsActive/BrickStyle/WireRed...），它没有、也不该知道
///   OTAPI 的 sTileHeader/bTileHeader 私有位布局。存原始位就等于把 OTAPI 的内部实现
///   固化进"中立格式"，管理器侧只能去复刻位运算并永久跟版本同步 —— 这正是要避免的耦合。
///
/// 布局（全小端）：
///   [0..3]  magic "UTPL"
///   [4]     ver = 3
///   [5..12] int32 w, int32 h        —— 模板尺寸（图格）
///   Tile 段：w*h 条，每条：
///        byte 空标记：1 = 该格完全空白（到此为止）；0 = 后面跟记录
///        记录：
///          byte f1: bit0 Active, bit1 InActive, bit2 Actuator, bit3 HasWall,
///                   bit4 HasLiquid, bit5 InvisibleBlock, bit6 InvisibleWall, bit7 FullBrightBlock
///          byte f2: bit0 FullBrightWall, bits1-3 BrickStyle(0..5), bit4 WireRed,
///                   bit5 WireBlue, bit6 WireGreen, bit7 WireYellow
///          byte f3: bit0 HasExtraFrame（额外帧变体字段是否出现）
///          [Active]        ushort Type, int16 U, int16 V, byte TileColor
///          [HasWall]       ushort Wall, byte WallColor
///          [HasLiquid]     byte LiquidAmount, byte LiquidType
///          [HasExtraFrame] byte FrameNumber, byte WallFrameX, byte WallFrameY, byte WallFrameNumber
///     坐标系：U/V = Terraria 的 frameX/frameY（图格动画/家具帧）。
///     BrickStyle：0=满块 1=半砖 2=右上斜 3=左上斜 4=右下斜 5=左下斜（与 Terraria 存盘编码一致）。
///     LiquidType：0=None 1=Water 2=Lava 3=Honey **8=Shimmer**（沿用 Terraria 存盘编码；
///                 shimmer 在 Terraria 里是独立位，故不是 4）。
///     ★ HasExtraFrame 只由 OTAPI 端写（它保留 frameNumber/wallFrame* 随机变体）；
///       管理器端(TEdit)不保留这些字段，可以完全不写 → 文件更小、语义仍完整。
///
///   ★ 坐标口径：箱子/标牌/实体的 x,y 全部是【模板内相对坐标】（0..w-1 / 0..h-1），
///     文件里不含世界原点；贴到某处时一律 "贴入点 + 记录坐标"。三个记录段必须同口径。
///   然后是若干【段】：byte 段号 + 段内容；段号 0xFF = 结束。
///        1 = Chests   : int32 n; 每条 = int32 x, int32 y, string 名字, 40 x (int32 type, int32 stack, int32 prefix)
///        2 = Signs    : int32 n; 每条 = int32 x, int32 y, string 文本
///        3 = Entities : int32 n;
///             每条 = byte kind, int32 x, int32 y, byte 槽数, 槽数 x (int32 type, int32 stack, int32 prefix), byte on
///             kind: 1=ItemFrame 2=DisplayDoll 3=HatRack 4=FoodPlatter 5=LogicSensor 6=WeaponsRack
///             （★ 这是 .utpl **自己的编号**，与 Terraria 的 TileEntityType 枚举值**不一致**，必须显式映射：
///                .utpl 1→ItemFrame(1)  2→DisplayDoll(3)  3→HatRack(5)  4→FoodPlatter(6)
///                      5→LogicSensor(2) 6→WeaponsRack(4)
///                服务端口径见 VirtualWorldService.cs 的 Te* 常量；写错会导致实体类型张冠李戴。）
///   读取端遇到未知段号 → 段头是 int32 字节长度，真读跳过（向前兼容；GZipStream 不可 Seek）。
///
/// ★ 尺寸上限（防御坏文件）：W/H ≤ 8192；格子数 ≤ 4096*4096；段内条数 ≤ 1,000,000。
/// </summary>
internal static class UtplFormat
{
    public const uint Magic = 0x4C505455;    // "UTPL" 小端
    public const byte Version = 3;
    public const string Extension = ".utpl";

    public const byte SegChests = 1;
    public const byte SegSigns = 2;
    public const byte SegEntities = 3;
    public const byte SegEnd = 0xFF;

    // 与 Terraria / TEdit.Terraria.TileEntityType 的枚举值保持一致
    public const byte KindItemFrame = 1;
    public const byte KindDisplayDoll = 2;
    public const byte KindHatRack = 3;
    public const byte KindFoodPlatter = 4;
    public const byte KindLogicSensor = 5;
    public const byte KindWeaponsRack = 6;

    // 液体类型（Terraria 存盘编码；shimmer 是独立位所以是 8）
    public const byte LiquidNone = 0;
    public const byte LiquidWater = 1;
    public const byte LiquidLava = 2;
    public const byte LiquidHoney = 3;
    public const byte LiquidShimmer = 8;

    public const int MaxDim = 8192;
    public const int MaxCells = 4096 * 4096;
    public const int MaxRecords = 1_000_000;
    public const int MaxUnknownSegmentBytes = 256 * 1024 * 1024;   // 未知段最长 256MB（防坏文件）
    public const int ChestSlots = 40;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // ==================== 写 ====================

    /// <summary>把快照写成 .utpl（先写临时文件再改名，避免半个文件）。</summary>
    public static void Write(string path, UtplRegion region)
    {
        string tmp = path + ".tmp";
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

        using (FileStream fs = File.Create(tmp))
        using (GZipStream gz = new(fs, CompressionLevel.Optimal))
        using (BinaryWriter w = new(gz, Utf8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(region.W);
            w.Write(region.H);

            foreach (TileData t in region.Tiles)
            {
                if (IsEmpty(t)) { w.Write((byte)1); continue; }
                w.Write((byte)0);
                WriteTile(w, t);
            }

            WriteChests(w, region.Chests);
            WriteSigns(w, region.Signs);
            WriteEntities(w, region.Entities);
            w.Write(SegEnd);
        }

        if (File.Exists(path)) { File.Delete(path); }
        File.Move(tmp, path);
    }

    /// <summary>写一条语义化图格记录（见文件头注释的位定义）。</summary>
    internal static void WriteTile(BinaryWriter w, TileData t)
    {
        bool active = t.active();
        bool hasWall = t.wall != 0;
        bool hasLiquid = t.liquid != 0;

        byte f1 = 0;
        if (active) { f1 |= 0x01; }
        if (t.inActive()) { f1 |= 0x02; }
        if (t.actuator()) { f1 |= 0x04; }
        if (hasWall) { f1 |= 0x08; }
        if (hasLiquid) { f1 |= 0x10; }
        if (t.invisibleBlock()) { f1 |= 0x20; }
        if (t.invisibleWall()) { f1 |= 0x40; }
        if (t.fullbrightBlock()) { f1 |= 0x80; }

        byte style = BrickStyleOf(t);
        byte f2 = (byte)((style & 0x07) << 1);
        if (t.fullbrightWall()) { f2 |= 0x01; }
        if (t.wire()) { f2 |= 0x10; }
        if (t.wire2()) { f2 |= 0x20; }
        if (t.wire3()) { f2 |= 0x40; }
        if (t.wire4()) { f2 |= 0x80; }

        byte frameNumber = t.frameNumber();
        byte wfX = (byte)Math.Clamp(t.wallFrameX(), 0, 255);
        byte wfY = (byte)Math.Clamp(t.wallFrameY(), 0, 255);
        byte wfN = t.wallFrameNumber();
        bool hasExtra = frameNumber != 0 || wfX != 0 || wfY != 0 || wfN != 0;
        byte f3 = hasExtra ? (byte)0x01 : (byte)0;

        w.Write(f1);
        w.Write(f2);
        w.Write(f3);

        if (active)
        {
            w.Write(t.type);
            w.Write(t.frameX);
            w.Write(t.frameY);
            w.Write(t.color());
        }
        if (hasWall)
        {
            w.Write(t.wall);
            w.Write(t.wallColor());
        }
        if (hasLiquid)
        {
            w.Write(t.liquid);
            w.Write(LiquidTypeOf(t));
        }
        if (hasExtra)
        {
            w.Write(frameNumber);
            w.Write(wfX);
            w.Write(wfY);
            w.Write(wfN);
        }
    }

    /// <summary>OTAPI TileData → 中立 BrickStyle（0..5）。</summary>
    internal static byte BrickStyleOf(TileData t)
    {
        if (t.halfBrick()) { return 1; }
        byte slope = t.slope();
        return slope is >= 2 and <= 5 ? slope : (byte)0;
    }

    /// <summary>中立 BrickStyle → OTAPI TileData。</summary>
    internal static void ApplyBrickStyle(ref TileData t, byte style)
    {
        if (style == 1) { t.halfBrick(true); return; }
        if (style is >= 2 and <= 5) { t.slope(style); }
    }

    /// <summary>OTAPI 液体类型 → 中立液体类型（Terraria 存盘编码）。</summary>
    internal static byte LiquidTypeOf(TileData t)
    {
        if (t.shimmer()) { return LiquidShimmer; }
        byte lt = t.liquidType();
        // Terraria 内存里 0=水 1=熔岩 2=蜂蜜；存盘编码是 1=水 2=熔岩 3=蜂蜜
        return lt switch { 0 => LiquidWater, 1 => LiquidLava, 2 => LiquidHoney, _ => LiquidWater };
    }

    /// <summary>中立液体类型 → OTAPI TileData。</summary>
    internal static void ApplyLiquidType(ref TileData t, byte liquidType)
    {
        if (liquidType == LiquidShimmer) { t.liquidType(0); t.shimmer(true); return; }
        byte lt = liquidType switch { LiquidLava => 1, LiquidHoney => 2, _ => 0 };
        t.liquidType(lt);
    }

    private static void WriteChests(BinaryWriter w, List<UtplChest> chests)
    {
        w.Write(SegChests);
        w.Write(chests.Count);
        foreach (UtplChest c in chests)
        {
            w.Write(c.X);
            w.Write(c.Y);
            w.Write(c.Name ?? "");
            for (int i = 0; i < ChestSlots; i++)
            {
                (int type, int stack, int prefix) = i < c.Items.Length ? c.Items[i] : (0, 0, 0);
                w.Write(type);
                w.Write(stack);
                w.Write(prefix);
            }
        }
    }

    private static void WriteSigns(BinaryWriter w, List<UtplSign> signs)
    {
        w.Write(SegSigns);
        w.Write(signs.Count);
        foreach (UtplSign s in signs)
        {
            w.Write(s.X);
            w.Write(s.Y);
            w.Write(s.Text ?? "");
        }
    }

    private static void WriteEntities(BinaryWriter w, List<UtplEntity> ents)
    {
        w.Write(SegEntities);
        w.Write(ents.Count);
        foreach (UtplEntity e in ents)
        {
            w.Write(e.Kind);
            w.Write(e.X);
            w.Write(e.Y);
            w.Write((byte)Math.Min(255, e.Slots.Count));
            for (int i = 0; i < e.Slots.Count && i < 255; i++)
            {
                w.Write(e.Slots[i].type);
                w.Write(e.Slots[i].stack);
                w.Write(e.Slots[i].prefix);
            }
            w.Write(e.On);
        }
    }

    /// <summary>只读头部拿尺寸（玩法给玩家回话用；不加载整张模板）。失败返回 false。</summary>
    public static bool TryReadSize(string path, out int w, out int h)
    {
        w = h = 0;
        try
        {
            using FileStream fs = File.OpenRead(path);
            using GZipStream gz = new(fs, CompressionMode.Decompress);
            using BinaryReader r = new(gz, Utf8, leaveOpen: false);
            if (r.ReadUInt32() != Magic) { return false; }
            byte ver = r.ReadByte();
            if (ver < 1 || ver > Version) { return false; }
            w = r.ReadInt32();
            h = r.ReadInt32();
            return w > 0 && h > 0 && w <= MaxDim && h <= MaxDim;
        }
        catch { return false; }
    }

    // ==================== 读 ====================

    /// <summary>读取 .utpl；任何结构性问题抛 <see cref="InvalidDataException"/>（调用方转成失败结果）。</summary>
    public static UtplRegion Read(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using GZipStream gz = new(fs, CompressionMode.Decompress);
        using BinaryReader r = new(gz, Utf8, leaveOpen: false);

        if (r.ReadUInt32() != Magic) { throw new InvalidDataException("不是 .utpl 文件（magic 不匹配）"); }
        byte ver = r.ReadByte();
        if (ver != Version) { throw new InvalidDataException($"不支持的 .utpl 版本 {ver}（本端只支持 v{Version}）"); }

        int w = r.ReadInt32(), h = r.ReadInt32();
        if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim) { throw new InvalidDataException($"模板尺寸非法: {w}x{h}"); }
        if ((long)w * h > MaxCells) { throw new InvalidDataException($"模板格数过大: {w}x{h}"); }

        UtplRegion region = new() { X = 0, Y = 0, W = w, H = h, Tiles = new TileData[w * h] };
        for (int i = 0; i < region.Tiles.Length; i++)
        {
            byte empty = r.ReadByte();
            if (empty != 0) { continue; }
            region.Tiles[i] = ReadTile(r);
        }

        while (true)
        {
            byte seg = r.ReadByte();
            if (seg == SegEnd) { break; }
            switch (seg)
            {
                case SegChests: ReadChests(r, region); break;
                case SegSigns: ReadSigns(r, region); break;
                case SegEntities: ReadEntities(r, region); break;
                default:
                    // 未知段：段头是 int32 字节长度。★ GZipStream 不可 Seek → 必须真的读掉这些字节（向前兼容）
                    int len = r.ReadInt32();
                    if (len < 0 || len > MaxUnknownSegmentBytes) { throw new InvalidDataException($"未知段 {seg} 长度非法: {len}"); }
                    SkipBytes(r, len);
                    break;
            }
        }
        return region;
    }

    /// <summary>读一条语义化图格记录（见文件头注释的位定义）。</summary>
    internal static TileData ReadTile(BinaryReader r)
    {
        byte f1 = r.ReadByte();
        byte f2 = r.ReadByte();
        byte f3 = r.ReadByte();

        TileData t = default;
        bool active = (f1 & 0x01) != 0;
        bool hasWall = (f1 & 0x08) != 0;
        bool hasLiquid = (f1 & 0x10) != 0;

        if (active) { t.active(true); }
        if ((f1 & 0x02) != 0) { t.inActive(true); }
        if ((f1 & 0x04) != 0) { t.actuator(true); }
        if ((f1 & 0x20) != 0) { t.invisibleBlock(true); }
        if ((f1 & 0x40) != 0) { t.invisibleWall(true); }
        if ((f1 & 0x80) != 0) { t.fullbrightBlock(true); }
        if ((f2 & 0x01) != 0) { t.fullbrightWall(true); }

        ApplyBrickStyle(ref t, (byte)((f2 >> 1) & 0x07));
        if ((f2 & 0x10) != 0) { t.wire(true); }
        if ((f2 & 0x20) != 0) { t.wire2(true); }
        if ((f2 & 0x40) != 0) { t.wire3(true); }
        if ((f2 & 0x80) != 0) { t.wire4(true); }

        if (active)
        {
            t.type = r.ReadUInt16();
            t.frameX = r.ReadInt16();
            t.frameY = r.ReadInt16();
            t.color(r.ReadByte());
        }
        if (hasWall)
        {
            t.wall = r.ReadUInt16();
            t.wallColor(r.ReadByte());
        }
        if (hasLiquid)
        {
            t.liquid = r.ReadByte();
            ApplyLiquidType(ref t, r.ReadByte());
        }
        if ((f3 & 0x01) != 0)
        {
            t.frameNumber(r.ReadByte());
            t.wallFrameX(r.ReadByte());
            t.wallFrameY(r.ReadByte());
            t.wallFrameNumber(r.ReadByte());
        }
        return t;
    }

    private static void ReadChests(BinaryReader r, UtplRegion region)
    {
        int n = CheckCount(r.ReadInt32(), "箱子");
        for (int i = 0; i < n; i++)
        {
            UtplChest c = new() { X = r.ReadInt32(), Y = r.ReadInt32(), Name = r.ReadString() };
            for (int s = 0; s < ChestSlots; s++) { c.Items[s] = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32()); }
            region.Chests.Add(c);
        }
    }

    private static void ReadSigns(BinaryReader r, UtplRegion region)
    {
        int n = CheckCount(r.ReadInt32(), "标牌");
        for (int i = 0; i < n; i++)
        {
            region.Signs.Add(new UtplSign { X = r.ReadInt32(), Y = r.ReadInt32(), Text = r.ReadString() });
        }
    }

    private static void ReadEntities(BinaryReader r, UtplRegion region)
    {
        int n = CheckCount(r.ReadInt32(), "实体");
        for (int i = 0; i < n; i++)
        {
            UtplEntity e = new() { Kind = r.ReadByte(), X = r.ReadInt32(), Y = r.ReadInt32() };
            int slots = r.ReadByte();
            for (int s = 0; s < slots; s++) { e.Slots.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32())); }
            e.On = r.ReadBoolean();
            region.Entities.Add(e);
        }
    }

    /// <summary>读掉 n 个字节（GZipStream 不可 Seek，只能真读）。</summary>
    private static void SkipBytes(BinaryReader r, int n)
    {
        byte[] buf = new byte[8192];
        while (n > 0)
        {
            int take = r.Read(buf, 0, Math.Min(buf.Length, n));
            if (take <= 0) { throw new EndOfStreamException("模板在跳过未知段时提前结束"); }
            n -= take;
        }
    }

    private static int CheckCount(int n, string what)
    {
        if (n < 0 || n > MaxRecords) { throw new InvalidDataException($"{what}记录数非法: {n}"); }
        return n;
    }

    // ==================== 工具 ====================

    /// <summary>该格是否"完全空白"（无图格、无墙、无液体、无线/制动器/非激活）——空白格在文件里只占 1 字节。</summary>
    public static bool IsEmpty(TileData t) =>
        !t.active() && t.wall == 0 && t.liquid == 0 && !t.anyWire() && !t.actuator() && !t.inActive();

    /// <summary>
    /// 模板名净化（只留安全字符；拒绝空名与路径穿越）。
    /// </summary>
    public static string? SanitizeName(string? name)
    {
        string safe = (name ?? "").Trim();
        if (safe.Length == 0) { return null; }
        foreach (char c in Path.GetInvalidFileNameChars()) { safe = safe.Replace(c, '_'); }
        safe = safe.Replace('/', '_').Replace('\\', '_').Trim('.', ' ');
        if (safe.Length == 0 || safe is "." or "..") { return null; }
        if (safe.Length > 64) { safe = safe[..64]; }
        return safe;
    }
}
