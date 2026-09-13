using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     让 ACT5 的伪装 BOSS 房**真的按小怪房结算**。
///
/// ## 为什么必须单独打这个补丁（IL 实证）
/// 房间类型**不是**由调用方传的 <c>RoomType</c> 决定的：
/// <code>
///   RunManager.CreateRoom(RoomType roomType, …)   // 这个 roomType 只决定"建哪种房"，没进 CombatRoom
///   CombatRoom.get_RoomType() => Encounter.RoomType   // ← 真正生效的是 **encounter 自带的类型**
/// </code>
/// 所以拿 BOSS 的 encounter 建房，房间就一定是 BOSS 房 —— 实测后果（用户反馈）：
/// <code>
///   Creating NCombatRoom … encounter=SOUL_FYSH_BOSS
///   CHARACTER.IRONCLAD has won against encounter ENCOUNTER.SOUL_FYSH_BOSS
///   [BaseLib] Checking for additional interactions with THE_ARCHITECT      ← 本幕被当成最终 BOSS 收尾
///   Creating NCombatRoom with mode=VisualOnly encounter=THE_ARCHITECT_EVENT_ENCOUNTER
/// </code>
/// "异鱼打完直接进建筑师了" 就是这个：BOSS 房 → 终局奖励 → act5 是最后一层 → 直接进结局。
///
/// ## 口径（用户原话）
/// <i>"图标为BOSS图标，战斗为BOSSID的无奖励小怪"</i>
/// ⇒ 图标是 BOSS 图标（<see cref="Act5BossNode" /> 负责）、
///    战斗是 BOSS 的 encounter（<see cref="Act5EncounterPoolPatch" /> 负责）、
///    结算按**小怪房**（本补丁）：小怪级奖励、不触发终局/结束本幕。
///
/// ## ⚠️ 判据必须是**坐标**，不能是"名字在不在伪装名单里"（用户实测："打一个BOSS直接进建筑师"）
/// 曾经用 <c>Act5BossDisplay.IsDisguised(encounter)</c> 判：只要某一刻它返回 false
/// （名单与实际房间不一致、或该 BOSS 恰好又是本幕 boss 槽那一个），
/// 这一场就退回 BOSS 房 ⇒ 奖励界面走终局分支 ⇒ 本幕结束 ⇒ 最后一幕 ⇒ 直接进建筑师。
/// 现在改成 <b>"不在最终 BOSS 坐标 ⇒ 一律按小怪结算"</b> —— 与名单无关，永不漂移。
/// </summary>
[HarmonyPatch(typeof(CombatRoom), "get_RoomType")]
internal static class Act5DisguisedRoomTypePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CombatRoom __instance, ref RoomType __result)
    {
        if (__result != RoomType.Boss) return;

        // 只在 act5（本模组的传奇/神话幕）里生效
        var state = RunStateAccessor.GetCurrentState();
        if (state?.Act is not Act5Model) return;

        // ★ 最终 BOSS 节点（地图 BossMapPoint）保持 BOSS 房；其余一律小怪房
        if (Act5BossDisplay.IsAtFinalBossNode(state)) return;

        MainFile.DebugLog(
            $"[Act5] '{__instance.Encounter?.Id?.Entry}' 不在最终BOSS节点 ⇒ 房间类型 Boss → Monster（按小怪结算）");
        __result = RoomType.Monster;
    }
}
