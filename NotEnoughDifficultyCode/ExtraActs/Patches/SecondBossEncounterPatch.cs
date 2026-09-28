using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     双 BOSS 的<b>第二场内容</b>：显式取用该层的第二个 boss。
///
/// ## 现象（用户在日志里报的："之前打过的 BOSS 在第五幕重新出现了"）
/// 实测（同一局、同一会话，中间只有"打第一个 BOSS → 进合成火堆 → 点第二个 BOSS 节点"三步）：
/// <code>
/// 7132: won against encounter ENCOUNTER.KNOWLEDGE_DEMON_BOSS      ← act4 第一个 BOSS
/// 7151: Player vote changed ... coord: (53, 63)                   ← 点合成火堆（虚拟坐标）
/// 7407: Player vote changed ... coord: (3, 13)                    ← 点第二个 BOSS 节点
/// 7415: Creating NCombatRoom ... encounter=KNOWLEDGE_DEMON_BOSS   ← 又是同一个 BOSS ✗
/// </code>
///
/// ## 根因（IL / API 事实）
/// 原版 <c>RunManager.CreateRoom</c> 的取怪规则是
/// <c>model as EncounterModel ?? Act.PullNextEncounter(roomType)</c>，
/// 而 <c>PullNextEncounter(Boss)</c> 返回的是 <c>RoomSet.NextBossEncounter</c>
/// —— <b>整幕固定的那一个 boss</b>（= 第一个 BOSS）。
/// 原版自己的双 BOSS 之所以正确，是因为它走"打完第一个 BOSS 的奖励界面 → 继续"那条路，
/// 那条路把 <c>SecondBossEncounter</c> <b>显式当 model 传进 CreateRoom</b>。
/// 而本模组在两个 BOSS 之间插了合成火堆 ⇒ 玩家是**点地图上的第二个 BOSS 节点**过去的，
/// 走的是 <c>EnterMapCoord</c> → <c>EnterMapPointInternal</c> → <c>PullNextEncounter(Boss)</c>
/// ⇒ 拿到的是第一个 BOSS，于是"第二个 BOSS 打的是第一个 BOSS"。
///
/// ## 修法（只改"这一场用哪个 encounter"，不碰任何流程）
/// 在 <c>ActModel.PullNextEncounter</c> 上加前缀：当**正要进入的坐标 == 本层第二个 BOSS 坐标**
/// 且该层确实有第二个 boss 时，直接把 <c>SecondBossEncounter</c> 当结果返回（跳过原版取怪）。
/// 进入流程、房间创建、存档、地图状态全部仍由原版负责 —— 比"接管进房"安全得多。
///
/// "正在进入的坐标"由 <see cref="SecondBossEntry.RecordEnteringCoord" /> 在
/// <c>RunManager.EnterMapCoord</c> 前缀里登记（那条路我们已经接管了一部分，顺手记一个坐标）。
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
public static class SecondBossEncounterPullPatch
{
    [HarmonyPriority(Priority.First)]
    [HarmonyPrefix]
    public static bool Prefix(ActModel __instance, RoomType roomType, ref EncounterModel __result)
    {
        if (!PatchScope.IsEnabled) return true;
        if (roomType != RoomType.Boss) return true;

        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (!SecondBossEntry.IsEnteringSecondBoss(state)) return true;

            var second = __instance.SecondBossEncounter;
            if (second == null) return true;                 // 该层没有第二个 boss ⇒ 交回原版

            __result = second;
            MainFile.Logger.Info(
                $"[DoubleBoss] 第二个 BOSS 房内容取用第二 boss='{second.Id?.Entry}'" +
                "（原版 PullNextEncounter(Boss) 只会给 RoomSet 里那个固定 boss ⇒ 会与第一场重复）");
            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[DoubleBoss] 第二 boss 取用失败（交回原版）: {ex.Message}");
            return true;
        }
    }
}

/// <summary>"正在进入哪个地图坐标"的登记处（只在 <c>RunManager.EnterMapCoord</c> 前缀里写入）。</summary>
internal static class SecondBossEntry
{
    private static MapCoord? _enteringCoord;

    /// <summary>进入地图坐标前登记（<c>EnterMapCoord</c> 前缀调用）。</summary>
    public static void RecordEnteringCoord(MapCoord coord) => _enteringCoord = coord;

    /// <summary>当前这次进房是不是"正在进本层的第二个 BOSS 节点"。</summary>
    public static bool IsEnteringSecondBoss(RunState? state)
    {
        var second = state?.Map?.SecondBossMapPoint?.coord;
        if (second == null || _enteringCoord == null) return false;

        return _enteringCoord.Value.col == second.Value.col
               && _enteringCoord.Value.row == second.Value.row;
    }
}
