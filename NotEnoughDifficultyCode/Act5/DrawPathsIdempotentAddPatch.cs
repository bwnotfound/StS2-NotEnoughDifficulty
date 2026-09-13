using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     把 <c>NMapScreen.DrawPaths</c> 里那次<b>无检查的 <c>Dictionary.Add</c></b> 改成索引器赋值。
///
/// ## 为什么不能"清缓存"了事（实测结论）
/// 我先前猜"<c>SetMap</c> 被调用两次、<c>_paths</c> 没清空"，于是加了 prefix 清空 <c>_paths</c>。
/// 日志证明 prefix <b>确实跑了</b>，但崩溃依旧 —— 说明是<b>一次 <c>SetMap</c> 内部同一条边被画了两次</b>。
/// 对自定义地图来说这几乎是必然的：我按"一条直线"给节点都连了子边，
/// 而 <c>NMapScreen</c> 在遍历时会从两端各经过一次这条边。
///
/// ## 修法（最小侵入）
/// <c>DrawPaths</c> 的本质是 <c>_paths[ (from, to) ] = CreatePath(...)</c> —— 一个缓存写入。
/// 用 <c>Add</c> 只在"每对坐标最多出现一次"的原版地图上成立；换成索引器赋值后：
///   - 无冲突时：结果与 <c>Add</c> 完全一致（键不存在，等价于新增）
///   - 有冲突时：覆盖同一条边的绘制结果，而不是抛 <c>ArgumentException</c>
/// 所以对原版地图<b>行为不变</b>，对我们的直线地图则直接消除崩溃。
/// </summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), "DrawPaths")]
public static class DrawPathsIdempotentAddPatch
{
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var addMethod = AccessTools.Method(
            typeof(Dictionary<(MegaCrit.Sts2.Core.Map.MapCoord, MegaCrit.Sts2.Core.Map.MapCoord),
                IReadOnlyList<Godot.TextureRect>>), "Add");

        var setItem = AccessTools.Method(
            typeof(Dictionary<(MegaCrit.Sts2.Core.Map.MapCoord, MegaCrit.Sts2.Core.Map.MapCoord),
                IReadOnlyList<Godot.TextureRect>>), "set_Item");

        var replaced = 0;
        foreach (var ins in instructions)
        {
            if (addMethod != null && setItem != null &&
                ins.opcode == OpCodes.Callvirt && Equals(ins.operand, addMethod))
            {
                yield return new CodeInstruction(OpCodes.Callvirt, setItem);
                replaced++;
                continue;
            }

            yield return ins;
        }

        MainFile.Logger.Info(
            $"[DrawPathsFix] 已把 {replaced} 处 Dictionary.Add 改为索引器赋值（消除自定义地图的重复键崩溃）");
    }
}
