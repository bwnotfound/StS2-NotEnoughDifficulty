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
/// 1. 若 N10 生效：把第二个 boss <b>放回 act 索引 2（第 3 层）</b>；
/// 2. 清掉"基础游戏误加在<b>最后一幕</b>上的第二个 boss"（N10 生效与否都做，见下）；
/// 3. 其它层（act1/2/4）的第二个 boss <b>一律不动</b>——那是本 mod 按层开关
///    （<see cref="DoubleBossConfigPatch" />）或别的 mod（Ascension100 进阶 18 等）的安排，
///    无差别清空等于删别人的功能（2026-09-22「叫醒」时改掉了旧的全清逻辑）。
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
            // ★ 这里**不再"有人在改 GenerateRooms ⇒ 整套放权"**（2026-09-22「叫醒」）。
            //   放权的后果：Ascension100（工坊 3801607408）的 `Ascension18.DoubleBossPlus` 一装上，
            //   N10 的第 3 层钉位与 act5 槽位清理就全部失效（双 boss 被基础游戏丢在最后一幕）。
            //   见 DoubleBossConfigPatch 里同一处改动的说明。
            ModCompat.NoteCoexistence("N10 双 boss 钉位", ModCompat.RoomGenerationTarget);

            var state = RunStateAccessor.GetState(__instance);
            if (state?.Acts == null || state.Acts.Count == 0) return;

            var hasDoubleBossAscension = AscensionHelper.HasAscension(AscensionLevel.DoubleBoss);

            // 目标层：act 索引 2 = 第 3 层。列表比 3 短时（不该发生）退化为最后一个 act。
            // N10 未生效时不设"保留对象"（keep = null），清理逻辑另有"只碰本模组自己的追加幕"这道闸。
            var targetIndex = state.Acts.Count > Act3Index ? Act3Index : state.Acts.Count - 1;
            var keep = hasDoubleBossAscension ? state.Acts[targetIndex] : null;

            // ── 只清"基础游戏误加在**最后一幕**上的第二个 boss" ──
            //
            // ⚠️ 历史：基础游戏 GenerateRooms 的 N10 分支写死"最后一个 act"
            // （`actIndex == Acts.Count - 1`），而本 mod 的 act4/5 是**追加**到末尾的 ⇒ 那个位置
            // 变成了 act5，于是游戏给 act5 塞了一个第二 boss（实测是玩家刚在 act4 打过的重复 boss）。
            // 那一幕按设计永远是我们自己的 Act4/Act5 模型，且 act5 的第二场由本 mod 在建图前
            // 按名单定稿（见 Act5BossDisplay），所以**必须清掉**。
            //
            // ★ 但**不再无差别清空其它层**（旧代码是 `foreach (act) if (act != target) Clear()`）：
            //   其它层的第二 boss 要么是本 mod 按层开关放的（DoubleBossConfigPatch），
            //   要么是别的 mod 放的（Ascension100 进阶 18 的第二幕、BossGauntlet 等）。
            //   那些是别人的合法行为，无差别清掉 = 把别人的功能也一起删了（多 mod 环境下不可接受）。
            ClearMisplacedLastActSecondBoss(state, keep);

            if (!hasDoubleBossAscension)
            {
                MainFile.DebugLog(
                    "[N10DoubleBoss] N10 未生效：只清最后一幕的第二个 boss；其它层的第二 boss" +
                    "（别人的安排 / 本 mod 按层开关）一律不动");
                return;
            }

            // N10 生效 ⇒ keep 必然非 null（上面就是按 hasDoubleBossAscension 取的）
            var target = keep!;

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
    ///     清掉"基础游戏误加在最后一幕"的第二个 boss。
    ///     只对**本模组自己的追加幕**（<see cref="Act4Model" /> / <see cref="Act5Model" />）动手，
    ///     且当它就是 N10 的钉位目标（act 列表只有 3 幕时 = 第 3 层）时**不动**。
    /// </summary>
    private static void ClearMisplacedLastActSecondBoss(RunState state, ActModel? keep)
    {
        try
        {
            var acts = state.Acts;
            var last = acts[acts.Count - 1];

            if (ReferenceEquals(last, keep)) return;                  // 它就是钉位目标 ⇒ 保留
            if (last is not (Act4Model or Act5Model)) return;         // 不是我们的幕 ⇒ 不碰别人的安排

            if (TryClearSecondBoss(last))
            {
                MainFile.DebugLog(
                    $"[N10DoubleBoss] 已清掉基础游戏误加在最后一幕（{last.Id?.Entry}）上的第二个 boss");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[N10DoubleBoss] 清最后一幕第二 boss 失败: {ex.Message}");
        }
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
