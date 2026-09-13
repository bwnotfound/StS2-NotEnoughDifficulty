using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Map;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     修复自定义地图导致的 <c>_paths</c> 重复键崩溃。
///
/// ## 症状（实测）
/// <code>
/// ArgumentException: An item with the same key has already been added.
///   Key: (MapCoord (3, 0), MapCoord (3, 1))
///   at NMapScreen.DrawPaths(...)
///   at NMapScreen.SetMap(...)
/// </code>
///
/// ## 根因（从 IL 确认）
/// <c>NMapScreen.DrawPaths</c> 对每个子节点做的是<b>无检查的 <c>Dictionary.Add</c></b>：
/// <code>
///   _paths.Add((mapPoint.coord, child.coord), CreatePath(...));
/// </code>
/// 而 <c>SetMap(map, seed, clearDrawings)</c> 在 <c>clearDrawings == false</c> 时<b>不会清空</b> <c>_paths</c>。
/// 只要同一个地图被 <c>SetMap</c> 走两次（我们接管建图后确实会发生），第二次就必撞键 → 崩溃。
///
/// ## 修法
/// 在 <c>SetMap</c> 的 prefix 里先把 <c>_paths</c> 清空。
/// <b>对原版行为是幂等的</b>：紧接着的 <c>DrawPaths</c> 会把同样的边重新填一遍，
/// 所以哪怕在完全原版的地图上跑，结果也完全一致——这是"只清不再用"的缓存。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
public static class MapPathsDuplicateKeyFixPatch
{
    private static readonly FieldInfo? PathsField = AccessTools.Field(typeof(NMapScreen), "_paths");

    [HarmonyPriority(Priority.High)]
    [HarmonyPrefix]
    public static void ClearStalePaths(NMapScreen __instance)
    {
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(MapPathsDuplicateKeyFixPatch), () =>
        {
            var paths = PathsField?.GetValue(__instance);
            if (paths == null) return;

            var clear = AccessTools.Method(paths.GetType(), "Clear");
            if (clear == null) return;

            clear.Invoke(paths, null);
            MainFile.DebugLog("[MapPathsFix] 已清空 NMapScreen._paths（避免 DrawPaths 重复键崩溃）");
        });
    }
}
