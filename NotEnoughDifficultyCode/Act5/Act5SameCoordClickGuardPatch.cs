using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     地图点击的总闸：**不允许给自己正站着的坐标投票**。
///
/// ## 用户实测
/// <i>"ACT５有个问题．前往下一个地点时可以点击自己所在的地点，导致卡在这里无法出去"</i>
///
/// ## 为什么会点得动（IL 实证）
/// <c>NMapPoint.get_IsTravelable()</c> 的第一个分支是
/// <code>
///   if (_screen != null &amp;&amp; _screen.IsDebugTravelEnabled &amp;&amp; !_screen.IsTraveling) return true;
/// </code>
/// 开发者旅行（控制台 <c>travel</c> / 调试开关）开着时**绕过 <c>State</c> 判定**，
/// 于是"自己脚下的点"也算可通行；点下去 → <c>NMapScreen.OnMapPointSelectedLocally</c>
/// → 投一张"去当前坐标"的票 → <c>AddVisitedMapCoord</c> 早就返回过 false（已走过）⇒
/// 房间进不去、票却已经投出，玩家卡在地图里出不来。
/// 日志实证：<c>Player vote changed for 1: -&gt;MapVote (gen: 5 coord: (3, 1))</c>（人就在 (3,1)）。
///
/// ## 这道闸的作用
/// 不看 <c>IsTravelable</c>、不看调试开关，只看**坐标**：
/// 点到自己所在的坐标 ⇒ 直接吞掉，不投票、不切屏 ⇒ 永远卡不住。
/// 对原版节点同样生效（act5 的起点/先古节点也是原版节点，同样能被点到）。
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.OnMapPointSelectedLocally))]
internal static class Act5SameCoordClickGuardPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NMapScreen __instance, NMapPoint point)
    {
        var coord = point?.Point?.coord;
        if (coord == null) return true;

        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state?.CurrentMapCoord is not { } current) return true;

            // MapCoord 是结构体：坐标相等就是"同一格"
            if (current.col != coord.Value.col || current.row != coord.Value.row) return true;

            MainFile.DebugLog(
                $"[MapGuard] 忽略对「当前所在坐标」的点击 ({coord.Value.col},{coord.Value.row}) —— 不投票");
            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[MapGuard] 判定失败（放行给原版）: {ex.Message}");
            return true;
        }
    }
}
