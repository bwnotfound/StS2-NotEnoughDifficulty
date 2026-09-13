using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     把 N10（进阶 10）的双 boss <b>按回第 3 层</b>。
///
/// ## 游戏原本怎么做的（从 RunManager.GenerateRooms 的 IL 读出）
/// <code>
///   V_7 = (actIndex == State.Acts.Count - 1);          // 只认「最后一个 act」
///   if (V_7 &amp;&amp; AscensionManager.HasLevel(AscensionLevel.DoubleBoss)) {
///       var second = rng.NextItem(act.AllBossEncounters.Where(b =&gt; b != act.BossEncounter));
///       act.SetSecondBossEncounter(second);
///   }
/// </code>
/// 也就是说双 boss 的位置<b>写死在"最后一个 act"</b>，而不是"第 3 层"。
/// 本 mod 用 <see cref="ExpandActListPatch" /> 把 act4/5 <b>追加</b>到 act 列表后，
/// "最后一个 act"从 act3 变成了 act5 —— 于是 N10 的双 boss 被顺延到了第 5 层。
///
/// ## 本 patch 做什么
/// 既然双 boss 是对玩家的考验，就让它回到它该在的地方（第 3 层）：
/// 1. 若 N10 生效：把第二个 boss <b>放回 act 索引 2（第 3 层）</b>，并清掉其它层上被游戏误加的第二个 boss
/// 2. 若 N10 未生效：清掉任何第二个 boss（本 mod 不允许非 N10 时凭空出现双 boss）
///
/// 结果：<b>不管 act 列表被追加了多长，N10 双 boss 永远落在第 3 层</b>。
///
/// ## ⚠️ act5 必须一起清（这里踩过坑）
/// 基础游戏的 N10 分支挂在「最后一个 act」上；act4/5 被追加后那个位置就是 <b>act5</b>，
/// 所以游戏总会给 act5 也塞一个第二 boss（实测是玩家刚在 act4 打过的重复 boss）。
/// 早期版本把 act5 排除在清理之外（怕丢了合成火堆的锚点），后果是
/// <c>EnsureAct5SecondBossAnchor</c> 看到 <c>HasSecondBoss=true</c> 就跳过
/// → act5 第二场变成重复 boss。
/// 正确做法：<b>act5 也清</b>——它的第二 boss 由本 mod 在<b>建图前</b>按名单定稿
/// （见 <see cref="Act5BossDisplay" />），而 <c>SecondBossMapPoint</c> 由 act5 自己的
/// 自定义地图提供，火堆锚点不受影响。
///
/// ## 优先级（用户确认的口径）
/// <b>N10 生效时 act3 默认关闭</b>：即 N10 的第二个 boss 归 act3，act3 的按层配置开关
/// （<c>Act3_DoubleBoss</c>）不再参与（<c>DoubleBossConfigPatch</c> 里显式跳过 act3）。
///
/// ## 多人安全
/// 全程只用 <c>state.Rng.UpFront</c>（run 级、两端同步）与确定性的池过滤，
/// 没有引入新随机源 → host/client 结果一致。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
[HarmonyAfter("BaseLib")]
public static class N10DoubleBossAtAct3Patch
{
    /// <summary>第 3 层在 act 列表里的索引。</summary>
    private const int Act3Index = 2;

    /// <summary>N10（AscensionLevel.DoubleBoss）是否生效。供其它 patch 判断"第 3 层是否必然有双 boss"。</summary>
    public static bool IsN10Active()
    {
        try { return AscensionHelper.HasAscension(AscensionLevel.DoubleBoss); }
        catch { return false; }
    }

    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void PinDoubleBossToAct3(RunManager __instance)
    {
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(N10DoubleBossAtAct3Patch), () =>
        {
            // 兼容性放权：有别的 mod 在管房间生成时，act3 的双 boss 归它安排。
            if (ModCompat.SomeoneElsePatchesRoomGeneration("N10 双 boss 钉位"))
                return;

            var state = RunStateAccessor.GetState(__instance);
            if (state?.Acts == null || state.Acts.Count == 0) return;

            var hasDoubleBossAscension = AscensionHelper.HasAscension(AscensionLevel.DoubleBoss);

            if (!hasDoubleBossAscension)
            {
                ClearAllSecondBosses(state, "N10 未生效");
                return;
            }

            // 目标层：act 索引 2 = 第 3 层。列表比 3 短时（不该发生）退化为最后一个 act。
            var targetIndex = state.Acts.Count > Act3Index ? Act3Index : state.Acts.Count - 1;
            var target = state.Acts[targetIndex];

            // 先把其它层上被游戏误加的第二个 boss 清掉（**含被追加的 act5 上那个**）。
            //
            // ⚠️ 这里曾经把 act5 排除在外（`act is not Act5Model`），理由是"act5 需要
            // SecondBossMapPoint 当合成火堆锚点"。那个理由是错的，后果是：
            //   基础游戏 GenerateRooms 的 N10 分支给"最后一个 act"（= 被追加后的 act5）
            //   塞了一个 boss（实测是玩家刚在 act4 打过的 TEST_SUBJECT_BOSS），
            //   而 act5 永远不清 → EnsureAct5SecondBossAnchor 看到 HasSecondBoss 就跳过
            //   → act5 第二场变成**重复 boss**。
            // 现在 act5 一并清掉：act5 出生时没有第二个 boss，由本 mod 在**建图前**
            // （BossGauntletStylePatches.RunManagerGenerateMapPrefix）按名单定稿
            // （见 Act5BossDisplay）。act5 自己会提供 SecondBossMapPoint，火堆锚点不受影响。
            foreach (var act in state.Acts)
                if (!ReferenceEquals(act, target))
                    TryClearSecondBoss(act);

            MainFile.DebugLog(
                "[N10DoubleBoss] 已清掉其它层的第二个 boss（含基础游戏误加在 act5 上的；" +
                "act5 的第二场由本 mod 在建图前按名单定稿）");

            if (target.HasSecondBoss)
            {
                MainFile.DebugLog(
                    $"[N10DoubleBoss] 第 3 层已有第二个 boss='{target.SecondBossEncounter?.Id?.Entry}'，保持");
                return;
            }

            var pool = BuildSecondBossPool(target);
            if (pool.Count == 0)
            {
                MainFile.Logger.Warn("[N10DoubleBoss] 第 3 层没有可用的第二 boss（池子只剩首个）");
                return;
            }

            var rng = state.Rng?.UpFront;
            var second = rng != null ? rng.NextItem(pool) : pool[0];
            target.SetSecondBossEncounter(second!);

            MainFile.DebugLog(
                $"[N10DoubleBoss] N10 双 boss 已按回第 3 层：首个='{target.BossEncounter?.Id?.Entry}' " +
                $"第二='{second!.Id.Entry}'（候选池 {pool.Count}）");
        });
    }

    /// <summary>
    ///     第二个 boss 的候选池 = 该 act 的 boss 池 − 它的首个 boss − 其它 act 已用的 boss。
    ///     与 <see cref="DeduplicateCustomActBossesPatch" /> 保持同一口径，避免跨层重复。
    /// </summary>
    private static List<EncounterModel> BuildSecondBossPool(ActModel act)
    {
        var exclude = new HashSet<string>(StringComparer.Ordinal);

        var first = act.BossEncounter?.Id?.Entry;
        if (!string.IsNullOrEmpty(first)) exclude.Add(first);

        var state = RunStateAccessor.GetCurrentState();
        if (state?.Acts != null)
        {
            foreach (var other in state.Acts)
            {
                if (ReferenceEquals(other, act)) continue;
                var id = other?.BossEncounter?.Id?.Entry;
                if (!string.IsNullOrEmpty(id)) exclude.Add(id);
            }
        }

        return (act.AllBossEncounters ?? Enumerable.Empty<EncounterModel>())
            .Where(b => b?.Id?.Entry is { } id && !exclude.Contains(id))
            .ToList();
    }

    private static void ClearAllSecondBosses(RunState state, string reason)
    {
        var cleared = 0;
        foreach (var act in state.Acts)
            if (TryClearSecondBoss(act)) cleared++;

        if (cleared > 0)
            MainFile.DebugLog($"[N10DoubleBoss] {reason}：清掉 {cleared} 个被误加的第二个 boss（act5 的保留不动）");
    }

    private static bool TryClearSecondBoss(ActModel? act)
    {
        try
        {
            if (act == null || !act.HasSecondBoss) return false;
            act.SetSecondBossEncounter(null!);
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[N10DoubleBoss] 清除 {act?.Id?.Entry} 的第二个 boss 失败: {ex}");
            return false;
        }
    }
}
