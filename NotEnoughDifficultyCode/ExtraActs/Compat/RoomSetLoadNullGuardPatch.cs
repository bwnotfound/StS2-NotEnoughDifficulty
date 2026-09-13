using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     读档防御兜底（坏档修复）。
///
///     ## 现象
///     启用本 mod 的存档「保存退出 → 继续」时崩溃：
///     <code>
///     ArgumentNullException: source
///       at Enumerable.Select(...)
///       at RoomSet.FromSave(SerializableRoomSet save)
///       at ActModel.FromSave(...)
///       at RunState.FromSerializable_Patch4(...)   // 被其它 mod patch 过
///     </code>
///
///     ## 根因（结合调用栈）
///     <c>RoomSet.FromSave</c> 对 <c>save.EventIds / NormalEncounterIds / EliteEncounterIds</c> 之一
///     调用 <c>.Select(...)</c>，而该列表为 <b>null</b>。这几个是 SerializableRoomSet 的无默认值
///     auto-property，序列化时没被填就是 null。栈里 <c>FromSerializable_Patch4</c> + CznCore 预加载链
///     表明<b>另一个 mod 在拦截 RunState.FromSerializable 时丢了数据</b>——很可能是它不认识本 mod 注入的
///     额外 act（act4/5），处理这些 act 的 RoomSet 时把 id 列表丢成了 null。本 mod 自身不 patch 序列化。
///
///     ## 兜底策略
///     - <see cref="RoomSetFromSaveNullGuardPatch"/>：FromSave 前把 null 的 id 列表补成空列表，
///       消除 <c>ArgumentNullException</c> 硬崩，让存档至少能读进来。
///     - <see cref="ActModelFromSaveRebuildPatch"/>：读档后若 mod act（act4/5）的 encounter/event/boss
///       为空或未设置，按该 act 池<b>确定性重建</b>，避免进房间时 <c>NextNormalEncounter</c> 除零崩。
///
///     ## 历史背景
///     早期只有 null→空 的止崩，曾留下隐患：若受影响的是<b>当前/即将进入</b>的 act，空 encounter 列表
///     进房间时（<c>NextNormalEncounter</c> 用 <c>list[i % list.Count]</c>）仍会除零崩。后续真实复现了
///     这一崩溃（玩家读档进 act5 第一场战斗 → DivideByZeroException @ RoomSet.NextNormalEncounter），
///     故补上了 <see cref="ActModelFromSaveRebuildPatch"/> 的二段重建。对已通关的旧 base act，空列表无害，
///     本 mod 不重建 base act（仅诊断）。
///     - 这两个 patch 是<b>纯防御</b>，不受难度总开关影响、始终生效（只在出现 null/空时动作）。
/// </summary>
[HarmonyPatch(typeof(RoomSet), nameof(RoomSet.FromSave))]
public static class RoomSetFromSaveNullGuardPatch
{
    [HarmonyPrefix]
    public static void Prefix(SerializableRoomSet save)
    {
        if (save == null) return;

        try
        {
            var fixedLists = new List<string>();

            if (save.EventIds == null)
            {
                save.EventIds = new List<ModelId>();
                fixedLists.Add("EventIds");
            }

            if (save.NormalEncounterIds == null)
            {
                save.NormalEncounterIds = new List<ModelId>();
                fixedLists.Add("NormalEncounterIds");
            }

            if (save.EliteEncounterIds == null)
            {
                save.EliteEncounterIds = new List<ModelId>();
                fixedLists.Add("EliteEncounterIds");
            }

            if (fixedLists.Count > 0)
            {
                MainFile.Logger.Warn(
                    "RoomSet.FromSave: 存档里有 null id 列表 [" + string.Join(", ", fixedLists) +
                    "]，已补空以避免读档崩溃（疑似其它 mod 在 FromSerializable 上丢了 run 数据）");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"RoomSetFromSaveNullGuardPatch failed: {ex}");
        }
    }
}

/// <summary>
///     读档后二段兜底：mod act（act4/5）的 encounter / event / boss 若在存档里丢失而读出空，
///     按该 act 的池子<b>确定性重建</b>，避免进房间时 <c>NextNormalEncounter</c>（<c>list[i % list.Count]</c>）
///     除零崩，或访问未设置的 <c>Boss</c> 抛 InvalidOperationException。
///
///     重建口径与正常生成（CustomActEncounterReplacementPatch）一致：
///     - act5 的 normal/elite ← 1+2+3 的 boss 混合池；act4 的 elite ← 1+2+3 的 elite 混合池。
///     - events ← 该 act 自身 <c>AllEvents</c>（已是加权混合）；boss ← 该 act 自身 <c>AllBossEncounters</c> 首项。
///     重建按 act 池稳定顺序去重、不用 rng，多人 host/client 结果一致。仅在列表为空 / boss 未设置时动作，
///     不覆盖正常读出的数据。非 mod act 只诊断不重建。
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.FromSave))]
public static class ActModelFromSaveRebuildPatch
{
    private static readonly System.Reflection.FieldInfo? RoomsField =
        AccessTools.Field(typeof(ActModel), "_rooms");

    private static readonly System.Reflection.FieldInfo? BossField =
        AccessTools.Field(typeof(RoomSet), "_boss");

    [HarmonyPostfix]
    public static void Postfix(ActModel __result)
    {
        try
        {
            if (__result == null) return;
            if (RoomsField?.GetValue(__result) is not RoomSet rooms) return;

            var isAct5 = __result is Act5Model;
            var isAct4 = __result is Act4Model;

            // 非 mod act：只诊断、不重建（不改 base act 行为）。
            if (!isAct4 && !isAct5)
            {
                var n = rooms.normalEncounters?.Count ?? 0;
                var e = rooms.eliteEncounters?.Count ?? 0;
                if (n == 0 || e == 0)
                    MainFile.Logger.Warn(
                        $"ActModel.FromSave: base act '{__result.Id?.Entry ?? "?"}' 读出空 encounter " +
                        $"(normal={n}, elite={e})；本 mod 不重建 base act，若进该 act 崩请反馈。");
                return;
            }

            var actId = __result.Id?.Entry ?? "?";

            // —— normal / elite ——（按正常生成口径用 1+2+3 混合池）
            if (isAct5)
            {
                FillIfEmpty(rooms.normalEncounters, () => MixEncounters(a => a.AllBossEncounters), actId, "normal");
                FillIfEmpty(rooms.eliteEncounters, () => MixEncounters(a => a.AllBossEncounters), actId, "elite");
            }
            else // Act4Model
            {
                FillIfEmpty(rooms.eliteEncounters, () => MixEncounters(a => a.AllEliteEncounters), actId, "elite");
            }

            // —— events ——（该 act 自身池；空了进 ? 节点同样会除零）
            if (rooms.events != null && rooms.events.Count == 0)
            {
                var ev = __result.AllEvents?.Where(x => x != null).ToList() ?? new List<EventModel>();
                if (ev.Count > 0)
                {
                    rooms.events.AddRange(ev);
                    MainFile.Logger.Warn(
                        $"ActModel.FromSave: act '{actId}' events 为空，已按 AllEvents 重建 {ev.Count} 项。");
                }
            }

            // —— boss ——（_boss 未设置时用该 act boss 池兜一个，避免 Boss getter 抛异常）
            if (BossField != null && BossField.GetValue(rooms) == null)
            {
                var boss = __result.AllBossEncounters?.FirstOrDefault(x => x != null);
                if (boss != null)
                {
                    rooms.Boss = boss;
                    MainFile.Logger.Warn(
                        $"ActModel.FromSave: act '{actId}' boss 未设置，已用 AllBossEncounters 首项兜底（{boss.Id.Entry}）。");
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"ActModelFromSaveRebuildPatch failed: {ex}");
        }
    }

    /// <summary>把 1+2+3 源 act 的某 encounter 池去重合并（确定性顺序，无 rng）。
    /// [第二轮改造] 已去掉「敌人移除列表」过滤——该功能整体删除。</summary>
    private static List<EncounterModel> MixEncounters(Func<ActModel, IEnumerable<EncounterModel>> select)
    {
        var outp = new List<EncounterModel>();
        var seen = new HashSet<string>();
        foreach (var act in new[] { RunProgress.GetRepresentativeAct(1), RunProgress.GetRepresentativeAct(2), RunProgress.GetRepresentativeAct(3) })
        {
            if (act == null) continue;
            foreach (var enc in select(act))
                if (enc?.Id.Entry is string k && seen.Add(k))
                    outp.Add(enc);
        }

        return outp;
    }

    private static void FillIfEmpty(
        List<EncounterModel> list, Func<List<EncounterModel>> poolFactory, string actId, string tier)
    {
        if (list == null || list.Count > 0) return;

        var pool = poolFactory();
        if (pool.Count == 0)
        {
            MainFile.Logger.Error(
                $"ActModel.FromSave: act '{actId}' {tier} 列表为空、且重建池也为空，无法兜底——进该房间仍会除零崩。");
            return;
        }

        list.AddRange(pool);
        MainFile.Logger.Warn(
            $"ActModel.FromSave: act '{actId}' {tier} encounter 列表为空（疑似坏档/其它 mod 丢数据），" +
            $"已按 act 池确定性重建 {pool.Count} 项以防 NextNormalEncounter 除零崩。");
    }
}