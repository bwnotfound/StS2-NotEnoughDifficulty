using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     **每层的全部决策，只在一处做**（用户方案："前往每一层的黑屏时间才是模组进行判断与构筑的时间"）。
///
/// ## 时机（从 <c>RunManager.EnterAct</c> 的 IL 读出来的真实顺序）
/// <code>
///   FadeOut()                       ← 黑屏开始
///   SetActInternal(actIndex)        ← act 状态切换完成（本类就挂在这里的 postfix）
///   ActEntered?.Invoke()            ← 公开事件，紧随其后
///   FadeIn(doTransition)            ← 黑屏结束
///   Hook.AfterActEntered(state)     ← 已经在黑屏之后了
/// </code>
/// 所以本类跑在**黑屏内**：玩家看不见，慢一点没关系，而且此时
/// <c>state.Act</c> 已经是新层、地图**还没生成** —— 一切都可以在此之前定稿，
/// 于是地图界面第一次画出来就是对的（不用像以前那样在地图生成后再补改）。
///
/// ## 为什么要有这个类
/// 以前每层要算的东西散在 4 个 patch 里（<c>GenerateMap</c> prefix / postfix、<c>SetMap</c> postfix …），
/// 各自"补一刀"，靠优先级和调用顺序互相依赖，出一堆时序 bug。
/// 现在**每层只调用一次 <see cref="Build" />**，按层把该做的做完，日志里一眼能看出这层算了什么。
///
/// ## 各处决策的归属
/// <list type="table">
///   <item><term>act4</term><description>顶端 boss 去重定稿（<c>EnsureAct4TopBoss</c>）、精英池填充</description></item>
///   <item><term>act5</term><description>BOSS 名单冻结、清空第二 boss 槽位、RoomSet.Boss 指向第 1 场</description></item>
///   <item><term>其它层</term><description>只做诊断，不动任何东西</description></item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(RunManager), "SetActInternal")]
internal static class ActBlueprint
{
    /// <summary>已经构筑过的 (种子, 层) 集合，避免同层重复构筑。</summary>
    private static readonly HashSet<string> _built = new(StringComparer.Ordinal);

    /// <summary>新 run 时清空记录。</summary>
    internal static void Reset()
    {
        _built.Clear();
        ActDepthPatch.Reset();       // 深度试算缓存也要按 run 清
        Act5BossDisplay.Reset();     // act5 抽到的两个伪装BOSS 也要按 run 清
    }

    /// <summary><c>SetActInternal</c> 的 postfix —— 黑屏窗口内，act 已切换、地图未生成。</summary>
    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void SetActInternalPostfix(RunManager __instance)
    {
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(ActBlueprint), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            if (state == null) return;

            var actIdx = RunProgress.GetActIndex(state);
            if (actIdx < 1) return;

            var key = $"{state.Rng?.StringSeed}|{actIdx}";
            if (!_built.Add(key))
            {
                MainFile.DebugLog($"[Blueprint] 第 {actIdx} 层已构筑过，跳过");
                return;
            }

            Build(state, actIdx);
        });
    }

    /// <summary>按层把这层该算的算完。**只在这里调用一次**（每层）。</summary>
    internal static void Build(RunState state, int actIdx)
    {
        MainFile.DebugLog(
            $"[Blueprint] === 第 {actIdx} 层构筑开始（黑屏窗口内，地图尚未生成）===");

        try
        {
            switch (actIdx)
            {
                case 4:
                    BuildAct4(state);
                    break;
                case 5:
                    BuildAct5(state);
                    break;
                default:
                    MainFile.DebugLog($"[Blueprint] 第 {actIdx} 层：原版层，不介入");
                    break;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Blueprint] 第 {actIdx} 层构筑失败: {ex}");
        }

        MainFile.DebugLog($"[Blueprint] === 第 {actIdx} 层构筑结束 ===");
    }

    // ============================================================
    // act4：顶端 boss 去重 + 精英池
    // ============================================================

    private static void BuildAct4(RunState state)
    {
        var act4 = state.Acts.OfType<Act4Model>().FirstOrDefault();
        if (act4 == null)
        {
            MainFile.Logger.Warn("[Blueprint] act4 实例取不到，跳过");
            return;
        }

        // 顶端 boss 去重定稿（避开前 3 层已分配的 boss、以及本层自己的第二 boss）
        DeduplicateCustomActBossesPatch.EnsureAct4TopBoss(act4, state);

        MainFile.DebugLog(
            $"[Blueprint] act4: 顶端 boss='{act4.BossEncounter?.Id?.Entry ?? "null"}' " +
            $"第二 boss='{(act4.HasSecondBoss ? act4.SecondBossEncounter?.Id?.Entry : "(无)")}'");
    }

    // ============================================================
    // act5：只有一个真 BOSS 槽位（用户口径：不要 N10 / 双重 BOSS）
    // ============================================================

    private static void BuildAct5(RunState state)
    {
        var act5 = state.Acts.OfType<Act5Model>().FirstOrDefault();
        if (act5 == null)
        {
            MainFile.Logger.Warn("[Blueprint] act5 实例取不到，跳过");
            return;
        }

        // ⚠️ 用户口径：**act5 只有一个真 BOSS，就是最后那个**。
        //    所以 <c>BossEncounter</c> 指向**最终 BOSS**（地图的 BossMapPoint = 通关触发点），
        //    伪装 BOSS 由 <see cref="Act5MidBoss" /> 注入节点、
        //    由 <see cref="Act5EncounterPoolPatch" /> 供给房间内容。
        //
        //    **不设** <c>SecondBossEncounter</c> —— 明确不用双重BOSS/N10 机制。
        var extreme = ExtraActsConfig.GetAct5Mode() == Act5Mode.Extreme;

        // BOSS 编排（考验/极限两档），在**地图生成之前**定稿：
        //   考验：一层随机 → 二层随机 → 三层当前分配的那个（最终BOSS）
        //   极限：一~二~三 循环取"没打过的"，三层那个压轴当最终BOSS，两场之间夹火堆
        // 判据 = RunProgress.GetDefeatedEncounterIds（地图历史里的 encounter id，唯一口径），
        // 随机源 = run seed 派生流（两端一致）。
        Act5BossDisplay.BuildPlan(state, extreme);

        var final = Act5BossDisplay.Finale;
        if (final == null)
        {
            MainFile.Logger.Error("[Blueprint] act5 最终 BOSS 取不到，本层可能无法收尾");
        }
        else
        {
            act5.SetBossEncounter(final);
            MainFile.DebugLog($"[Blueprint] act5 最终 BOSS = '{final.Id.Entry}'（唯一真 BOSS，BossMapPoint）");
        }

        // 确保第二 boss 槽位为空（N10 或别的 mod 可能塞过东西）
        if (act5.HasSecondBoss)
        {
            var stale = act5.SecondBossEncounter?.Id?.Entry;
            act5.SetSecondBossEncounter(null!);
            MainFile.DebugLog($"[Blueprint] act5 已清空第二 boss 槽位（原值='{stale}'）—— 不用双重BOSS机制");
        }

        MainFile.DebugLog(
            $"[Blueprint] act5 档位={(extreme ? "极限" : "考验")} " +
            $"伪装BOSS {Act5BossDisplay.DisguisedCount} 场（火堆间隔={Act5BossDisplay.HearthsBetween}）: " +
            string.Join(" → ", Enumerable.Range(0, Act5BossDisplay.DisguisedCount)
                .Select(i => $"'{Act5BossDisplay.GetDisguised(i)?.Id.Entry ?? "<空>"}'")));
    }
}
