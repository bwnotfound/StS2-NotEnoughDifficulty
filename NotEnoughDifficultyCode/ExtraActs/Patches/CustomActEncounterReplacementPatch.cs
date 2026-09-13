using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     填充自定义 act 的战斗内容池（第二轮改造）。
///
/// ## act4：用**全部基础精英的确定名单**填充（每个精英各一次）
/// 名单 = <see cref="Act4ElitePlan.Roster" />（按层 1→3 聚合 <c>AllEliteEncounters</c>，
/// 含整幕模组塞进同层的 act 变体，全局去重）。
/// **与"玩家打没打过"无关** —— 用户："就９个精英有点离谱"。
/// 实际每场打谁由 <see cref="Act4ElitePlanPickPatch" />（按坐标查落位表）决定。
///
/// ## act5
/// BOSS 槽位（伪 BOSS + 最终 BOSS）**不在这里填** —— 已收口到
/// <see cref="ActBlueprint" />（黑屏窗口内、每层只算一次），
/// 那边直接调 <c>ActModel.SetBossEncounter</c> / <c>SetSecondBossEncounter</c>。本类只管 act4。
///
/// ## Harmony ordering
/// <c>[HarmonyAfter("BaseLib")]</c>：BaseLib 的 ActModelGenerateRoomsPatch 也 patch 同名方法
/// （处理 Ancient 注入），它对 encounter 列表不动，跟我们正交；显式声明 After 保证我们最后改。
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GenerateRooms))]
[HarmonyAfter("BaseLib")]
public static class CustomActEncounterReplacementPatch
{
    private static readonly FieldInfo? RoomsField =
        AccessTools.Field(typeof(ActModel), "_rooms");

    /// <summary>把 RoomSet 的取怪计数器全部归零（替换列表后必须做，见 FillAct4Elites 注释）。</summary>
    private static void ResetVisitedCounters(RoomSet rooms)
    {
        foreach (var name in new[] { "normalEncountersVisited", "eliteEncountersVisited", "bossEncountersVisited" })
        {
            try
            {
                var f = AccessTools.Field(typeof(RoomSet), name);
                if (f == null)
                {
                    MainFile.Logger.Warn($"[Act4] 找不到 RoomSet.{name}，计数器未重置");
                    continue;
                }

                f.SetValue(rooms, 0);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"[Act4] 重置 {name} 失败: {ex}");
            }
        }
    }

    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void ReplaceEncounters(ActModel __instance, Rng rng)
    {
        if (!PatchScope.IsEnabled) return;
        if (__instance is not Act4Model) return;

        PatchScope.Run(nameof(CustomActEncounterReplacementPatch), () =>
        {
            var rooms = RoomsField?.GetValue(__instance) as RoomSet;
            if (rooms == null)
            {
                MainFile.Logger.Error(
                    $"Failed to access _rooms on {__instance.Id.Entry}; encounter replacement skipped");
                return;
            }

            FillAct4Elites(rooms);
        });
    }

    /// <summary>
    ///     act4：eliteEncounters = **全部基础精英的确定名单**（每个一次，不重复）。
    ///
    /// ⚠️ 旧实现的两个 bug（用户："为什么会有精英重复啊？"/"就９个精英有点离谱"）：
    /// <list type="number">
    ///   <item>名单取的是"**没打过**的精英" ⇒ 打过的就不在地图里了，
    ///         某局只剩 9 个（用户视角："就９个精英"）；</item>
    ///   <item><c>target = Math.Max(名单数, rooms.eliteEncounters.Count)</c> +
    ///         <c>Add(名单[i % 名单数])</c> 取模循环 ⇒ 池子里出现重复项，
    ///         原版再从这个池子随机抽 ⇒ 同一场精英打两遍。</item>
    /// </list>
    /// 现在：池子 = 名单（各一次），而**实际取怪**由 <see cref="Act4ElitePlanPickPatch" />
    /// 按坐标查落位表决定 ⇒ 原版随机抽取完全不参与，重复不可能发生。
    /// </summary>
    private static void FillAct4Elites(RoomSet rooms)
    {
        var roster = Act4ElitePlan.Roster();

        // 兜底：名单读不到（理论不该发生）时退回全部 base 精英
        if (roster.Count == 0)
        {
            MainFile.Logger.Error("[Act4] 精英名单为空，无法填充；交回原版池子");
            return;
        }

        rooms.eliteEncounters.Clear();
        rooms.eliteEncounters.AddRange(roster);

        // ★ 替换列表后必须归零计数器，否则 (visited % Count) 落在错误偏移上
        ResetVisitedCounters(rooms);

        MainFile.DebugLog(
            $"[Act4] eliteEncounters 填充 {roster.Count} 个（名单，各一次）: " +
            string.Join(", ", roster.Select(e => e.Id.Entry)));
    }
}
