using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     双 boss 按层开关（需求1）。
///
/// ## 背景：为什么需要这个开关
/// 进阶 10 的 <c>AscensionLevel.DoubleBoss</c> 只会给"最后一个 act"安排第二个 boss
/// （从 <c>RunManager.GenerateRooms</c> 的 IL 确认：判据是 <c>actIndex == Acts.Count - 1</c>）。
/// 本 mod 用 <see cref="ExpandActListPatch" /> 把 act4/5 <b>追加</b>到 act 列表之后，
/// "最后一个 act"变成了 act5 —— 于是原本属于 act3 的双 boss 被顺延到了 act5。
/// 这个开关把"哪一层出双 boss"的决定权交回玩家。
///
/// ## 时机
/// 本 patch 挂在 <c>RunManager.GenerateRooms</c>，此时 <c>state.Acts</c> 已定、
/// 各 act 的 <c>BossEncounter</c> 也已抽好（在 <see cref="DeduplicateCustomActBossesPatch" /> 之后），
/// 所以 <c>act.AllBossEncounters</c> 里"该 act 的 boss 池"是完整可用的
/// —— <b>包括别的模组注册进该 act 的 boss</b>（兼容整幕类模组的关键）。
///
/// ## 执行顺序（Harmony postfix 按 priority 升序跑：Last=0 最先 → First=800 最后）
/// | 顺序 | patch | priority |
/// |---|---|---|
/// | 1 | <see cref="DeduplicateCustomActBossesPatch" /> | Low(200) |
/// | 2 | <see cref="N10DoubleBossAtAct3Patch" /> | Low(200) |
/// | 3 | 本 patch | LowerThanNormal(300) |
///
/// ## 双 boss 之间的火堆
/// 不在这里做 —— 见 <c>SyntheticHearthPatch.cs</c>（合成节点法）。
/// 原先那个"找第二个 boss 的父节点改 PointType"的做法**已被证明必然失败**并删除：
/// <c>map.GetAllMapPoints()</c> 枚举的是 <c>Grid</c>，而 <c>SecondBossMapPoint</c> 不在 <c>Grid</c> 里，
/// 所以根本找不到父节点（日志原话：<c>act3 找不到第二个 boss 的父节点，火堆未插入</c>）。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
[HarmonyAfter("BaseLib")]
public static class DoubleBossConfigPatch
{
    [HarmonyPriority(Priority.LowerThanNormal)]
    [HarmonyPostfix]
    public static void ApplyDoubleBossConfig(RunManager __instance)
    {
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(DoubleBossConfigPatch), () =>
        {
            // ★ 这里**不再"有人在改 GenerateRooms ⇒ 整套放权"**（2026-09-22「叫醒」）。
            //
            //   真因：Ascension100（工坊 3801607408）的 `Ascension18.DoubleBossPlus` 也 patch 了
            //   `RunManager.GenerateRooms`，于是本补丁连同 <see cref="N10DoubleBossAtAct3Patch" />
            //   一起被放权跳过 ⇒ 玩家开了 Act1/2/4_DoubleBoss 也**一个双 boss 都不会有**
            //   （还能看到双 boss，是因为 RunManager.GenerateMap 前缀那条补救路径没放权）。
            //
            //   为什么可以共存（两边都是"礼让型"）：
            //     · 我们：本层**已经有**第二个 boss 就跳过（见下面 `if (act.HasSecondBoss)`）；
            //     · Ascension100 A18：`act.HasSecondBoss` 为真时返回 Unchanged。
            //   谁先跑都收敛到同一个结果：开关开的层由我们放，A18 只补它自己那一层（第二幕）。
            //   本补丁 `[HarmonyPriority(LowerThanNormal)]`（300）跑在 A18 的 Normal（400）**之前**，
            //   所以我们放完它就让位。
            ModCompat.NoteCoexistence("双 boss 分配", ModCompat.RoomGenerationTarget);

            var state = RunStateAccessor.GetState(__instance);
            if (state?.Acts == null) return;

            // ★ 诊断（一眼看出"每个 act 被判成第几层"）——模组 act 变体认不出层时，
            //   以前是**静默 -1**（用户："模组里面的 ACT 2 变种没有双 boss"），现在这里会写明。
            MainFile.DebugLog("[DoubleBoss] 本局 act 分层判定：" + string.Join(" | ",
                state.Acts.Select(a =>
                    $"{RunProgress.GetActIndexFromModel(a, state)}={a?.GetType().Name}" +
                    $"(Index={a?.Index.ToString() ?? "?"}, 第二boss={(a != null && a.HasSecondBoss ? a.SecondBossEncounter?.Id?.Entry ?? "有" : "无")})")));

            foreach (var act in state.Acts)
            {
                // ★ 层号**按数据判**（ActsByIndex / 本局 Acts / act.Index），显式传 state ——
                //   绝不能按 act 类型映射：那样别的模组的 act 变体一律认不出层，
                //   本层的双 boss 就静默失效（用户实测："模组里面的 ACT 2 变种没有双 boss"）。
                //   ⚠️ 配置槽位用 ActLayout.ConfigLayerOf：本模组的两个幕永远读 4/5 号开关
                //   （第 4 幕被心脏模组占用而顺延时，开关不会跑去管别人的幕）。
                var actIdx = ActLayout.ConfigLayerOf(act, state);
                if (actIdx < 1) continue;
                if (!IsDoubleBossEnabled(actIdx)) continue;

                // ── act3 特别处理（需求）──
                // 第 3 层的双 boss 由 N10 接管：N10 生效时该层必然已有第二个 boss
                //（由 N10DoubleBossAtAct3Patch 钉住），这里跳过，避免覆盖 N10 的抽取结果。
                // N10 未生效时本开关照常工作（想手动给第 3 层加双 boss 就打开它）。
                if (actIdx == 3 && N10DoubleBossAtAct3Patch.IsN10Active())
                {
                    MainFile.DebugLog(
                        "[DoubleBoss] act3：N10 已接管双 boss，本层配置开关跳过（避免覆盖 N10 的抽取结果）");
                    continue;
                }

                try
                {
                    // 兼容性：如果本层已经有第二个 boss（base game 安排的，或**别的 mod** 安排的，
                    // 例如 Boss Gauntlet 的 EnsureSecondBossForAct），就用它的、不去覆盖。
                    // 日志里把这个事实说清楚，避免"双 boss 没生效"时误判成我们的 bug。
                    if (act.HasSecondBoss)
                    {
                        MainFile.DebugLog(
                            $"[DoubleBoss] act{actIdx}: 已有第二个 boss='{act.SecondBossEncounter?.Id?.Entry}'" +
                            "（由 base game 或其它 mod 安排），本层跳过");
                        continue;
                    }

                    var first = act.BossEncounter?.Id?.Entry;

                    // ★ 走该 act 自己的 boss 池（而不是全局池差集）——
                    // 这样别的模组注册进这个 act 的 boss 也能成为候选（兼容性关键）。
                    // Ordinal 排序保证确定性；不用 rng（不吃随机流，两端各自算都一致）。
                    var pool = (act.AllBossEncounters ?? Enumerable.Empty<EncounterModel>())
                        .Where(b => b?.Id?.Entry is { } id && id != first)
                        .OrderBy(b => b.Id.Entry, StringComparer.Ordinal)
                        .ToList();

                    if (pool.Count == 0)
                    {
                        MainFile.Logger.Warn(
                            $"[DoubleBoss] act{actIdx} 没有可用的第二 boss（池里只剩首个），跳过");
                        continue;
                    }

                    var index = GetStableBossIndex(state, act, actIdx, pool.Count);
                    var second = pool[Math.Clamp(index, 0, pool.Count - 1)];
                    act.SetSecondBossEncounter(second);

                    MainFile.DebugLog(
                        $"[DoubleBoss] act{actIdx}: 首个='{first}' 第二='{second.Id.Entry}' " +
                        $"(该层 boss 池 {pool.Count} 个, 索引 {index})");
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Error($"[DoubleBoss] act{actIdx} 设置第二 boss 失败: {ex}");
                }
            }
        });
    }

    /// <summary>
    ///     本层是否走"原版双 boss 槽位"机制（配置开关）。
    ///     act3 在 N10 生效时由 N10 接管，不受本开关控制（见上面注释）。
    ///
    /// ⚠️ <b>act5 恒为 false</b>：第 5 层的连战**不用**这套槽位机制
    /// （<see cref="Act5BossSequence" /> 自己发房 + <c>WinRun()</c> 收官，地图上只留一个 BOSS 节点）。
    /// 若这里对 act5 返回 true，三处补救逻辑都会给 act5 塞回一个第二 boss —— 后果是
    /// 地图上多一个 BOSS 图标、并且**同一场 boss 会被打第二次**（用户反馈的"双重 BOSS 没改"）。
    ///
    /// 注意：这个函数**曾经同时是"合成火堆该不该注入"的判据**，所以
    /// <c>SyntheticHearth.InjectVisuals</c> 那边已改成显式排除 act5（火堆由连战自己控制）。
    /// </summary>
    internal static bool IsDoubleBossEnabled(int actIdx) => actIdx switch
    {
        1 => NotEnoughDifficultyConfig.Act1_DoubleBoss,
        2 => NotEnoughDifficultyConfig.Act2_DoubleBoss,
        3 => NotEnoughDifficultyConfig.Act3_DoubleBoss,
        4 => NotEnoughDifficultyConfig.Act4_DoubleBoss,
        _ => false   // act5：连战不走槽位机制
    };

    /// <summary>
    ///     确定性选取：把 <c>Rng.StringSeed | act.Id | 首个 boss Id | actIndex | 候选数</c>
    ///     拼成字符串做稳定散列（FNV-1a），映射到 [0, candidateCount)。
    ///
    /// <b>不调用 <c>rng.NextItem(...)</c></b> —— 不消耗游戏随机流，
    /// host/client 各自算结果一致，也不会打乱后续随机序列。
    /// （照搬工坊模组 Boss Gauntlet 的 <c>GetStableBossIndex</c> 思路。）
    /// </summary>
    private static int GetStableBossIndex(RunState state, ActModel act, int actIndex, int candidateCount)
    {
        if (candidateCount <= 1) return 0;

        var key = $"{state.Rng?.StringSeed}|{act.Id}|{act.BossEncounter?.Id}|{actIndex}|{candidateCount}";

        unchecked
        {
            uint hash = 2166136261u; // FNV-1a 32
            foreach (var ch in key)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return (int)(hash % (uint)candidateCount);
        }
    }
}
