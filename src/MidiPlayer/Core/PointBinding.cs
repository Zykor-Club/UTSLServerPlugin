namespace MidiPlayer.Core;

/// <summary>
/// 八音盒绑定的图格坐标（2×2 的左上角）。
///
/// ⚠️ 上游源码里**缺少这个类型的定义**（全项目只有 7 处使用、0 处定义，
/// 也就是说原始项目本身编译不过）。这里按使用方式补上：
///   - 需要 X / Y 可读；
///   - 需要值相等比较（代码里有 Bound == topLeft），
/// 因此用 record（**引用类型** + 自带值相等）。
/// 注意：必须是引用类型 —— 代码里直接把 Bound.X 用在 PointBinding? 上，
/// 如果是 struct，PointBinding? 就没有 .X 成员了。
/// </summary>
public sealed record PointBinding(int X, int Y);
