using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT5 的"小怪池" = 两个伪装 BOSS（按进度依次给）。
///
/// 原版建战斗房的路径是
/// <code>
///   RunManager.EnterMapPointInternal
///     → RunManager.CreateRoom(RoomType, MapPointType, model: null)
///       → ActModel.PullNextEncounter(roomType)
/// </code>
/// 而 act5 的普通遭遇池是**空的**（<c>GenerateAllEncounters</c> 只给 act3 的池，
/// 且 <c>NextNormalEncounter</c> 会对空池取模 → <c>DivideByZeroException</c>）。
/// 所以这里把 <c>Monster</c> 这一档接过来，其余档位一律交回原版。
///
/// 这样伪装 BOSS 房就是**原版的普通战斗房**：楼层推进、已走坐标、地图历史
/// （<c>RunProgress.GetDefeatedEncounterIds</c> 用的就是它）全部由原版记账，
/// 不需要任何"拦截点地图 → 手动进房"的旁路。
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
internal static class Act5EncounterPoolPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(ActModel __instance, RoomType roomType, ref EncounterModel __result)
    {
        if (__instance is not Act5Model || roomType != RoomType.Monster) return true;

        var state = RunStateAccessor.GetCurrentState();
        if (state?.Act is not Act5Model) return true;

        var encounter = Act5MidBoss.NextDisguisedEncounter(state);
        if (encounter == null) return true;   // 取不到就交回原版，别把问题掩盖成"静默给一场空房"

        __result = encounter;
        return false;
    }
}
