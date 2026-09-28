using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT5 的 BOSS 编排（**用户最终口径**）。
///
/// ## 两个档位
/// <list type="table">
///   <item><term>考验（<see cref="Act5Mode.Trial" />）</term><description>
///     先古之名 → 一层随机BOSS → 二层随机BOSS → 最终BOSS（三层当前分配的那个）。
///     两个伪装BOSS 之间不夹火堆。</description></item>
///   <item><term>极限（<see cref="Act5Mode.Extreme" />）</term><description>
///     先古之名 →(没打过的BOSS → 火堆)交替 → 最终BOSS，**火堆数 = 场数 − 1**；
///     取 BOSS 的顺序是 <b>一~二~三~一~二~三</b> 循环，且**三层 BOSS 必须压轴**当最终BOSS。</description></item>
/// </list>
///
/// ## 怎么挑（"我们对应的就是战斗ID"）
/// 1. **按层取池**：<see cref="RunProgress.GetBaseBossesByAct" />（走 <c>ModelDb.ActsByIndex</c>，
///    整幕类模组塞进同层的 act 变体也算）。
/// 2. **减掉打过的**：<see cref="RunProgress.GetDefeatedEncounterIds" /> —— 唯一权威口径是
///    <c>RunState.MapPointHistory</c> 里每场战斗的 <c>ModelId.Entry</c>
///    （**不加 RoomType 过滤**，否则被 mod 改过房间类型的战斗会漏掉，而伪装BOSS房正好就是那种）。
/// 3. 随机源 = <c>new Rng(state.Rng.Seed, "act5_boss_plan")</c>：只依赖已同步的 seed，
///    多人两端抽到同一套，不会 desync。
/// 4. 上限定在 <see cref="MaxDisguised" />（= 地图能给的位置数），超出的会在日志里提示丢弃。
///
/// ## 房间形态
/// 伪装 BOSS：图标是 BOSS 图标（<see cref="Act5BossNode" /> 按各自 encounter 上图标）、
/// 战斗是 BOSS 的 encounter（<see cref="Act5EncounterPoolPatch" /> 供给）、
/// 按小怪房结算并不结束本幕（<see cref="Act5DisguisedRoomTypePatch" /> /
/// <see cref="Act5DisguisedNoEndingPatch" />）。
/// 最终 BOSS：原生 <c>BossEncounter</c> + 地图 <c>BossMapPoint</c>，打赢才收尾。
/// </summary>
internal static class Act5BossDisplay
{
    /// <summary>伪装 BOSS 场数上限（分叉/蛇形布局 + 等比缩小后一列能容纳的场数）。</summary>
    public const int MaxDisguised = 10;

    private const string RngStreamName = "act5_boss_plan";

    private static readonly List<EncounterModel> Disguised = new();
    private static EncounterModel? _finale;
    private static bool _hearthsBetween;

    /// <summary>伪装 BOSS 场数。</summary>
    public static int DisguisedCount => Disguised.Count;

    /// <summary>两场之间要不要夹火堆（极限档 = 要）。</summary>
    public static bool HearthsBetween => _hearthsBetween;

    /// <summary>第 index 个伪装 BOSS。</summary>
    public static EncounterModel? GetDisguised(int index) =>
        index >= 0 && index < Disguised.Count ? Disguised[index] : null;

    /// <summary>压轴的最终 BOSS。</summary>
    public static EncounterModel? Finale => _finale;

    /// <summary>这套编排建好了吗（读档续玩时要重建）。</summary>
    public static bool HasPlan => _finale != null || Disguised.Count > 0;

    /// <summary>新 run / 重建前清空。</summary>
    public static void Reset()
    {
        Disguised.Clear();
        _finale = null;
        _hearthsBetween = false;
    }

    /// <summary>这个 encounter id 是**本次编排里**的伪装 BOSS 吗。</summary>
    public static bool IsDisguised(string? entry)
    {
        if (string.IsNullOrEmpty(entry)) return false;

        foreach (var chosen in Disguised)
        {
            var id = chosen?.Id?.Entry;
            if (!string.IsNullOrEmpty(id) && string.Equals(entry, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     玩家现在是不是站在**最终 BOSS 节点**上（地图 <c>BossMapPoint</c> 的坐标）。
    ///
    /// 这是"能不能结束本幕 / 这一场算不算 BOSS 房"的**唯一可靠判据**：
    /// 名字在不在伪装名单里会随名单变化而漂移，坐标不会
    /// （踩过：用名单判 ⇒ 某一刻判成"不是伪装BOSS" ⇒ 本幕被当成最终 BOSS 收尾 ⇒ 直接进建筑师）。
    /// </summary>
    public static bool IsAtFinalBossNode(RunState? state)
    {
        try
        {
            var coord = state?.CurrentMapCoord;
            var boss = state?.Map?.BossMapPoint?.coord;
            if (coord == null || boss == null) return true;      // 判不出来 → 当期就是"最终BOSS"（保守：不乱结束）

            return coord.Value.col == boss.Value.col && coord.Value.row == boss.Value.row;
        }
        catch
        {
            return true;
        }
    }

    // ============================================================
    // 编排
    // ============================================================

    /// <summary>
    ///     按档位排 BOSS。在 act5 的黑屏窗口内调用（<see cref="ActBlueprint" />）；
    ///     读档续玩时由 <see cref="Act5MidBoss" /> 在注入前补调一次。
    /// </summary>
    public static void BuildPlan(RunState state, bool extreme)
    {
        try
        {
            Reset();

            var pools = RunProgress.GetBaseBossesByAct();      // [0]=1层 [1]=2层 [2]=3层
            // ★ 只按"**进入本幕之前**"的战绩算名单 ⇒ 整幕内名单恒定，
            //   不会因为"边打边少"而漂移（踩过：打第 4 个伪装BOSS 时名单已变，
            //   那一场被判成"不是伪装BOSS" ⇒ 按 BOSS 房收尾 ⇒ 直接进建筑师）。
            var defeated = RunProgress.GetDefeatedEncounterIdsBeforeCurrentAct(state);
            var rng = new Rng(state.Rng.Seed, RngStreamName);

            // 本次编排已经选中的（防止"同一个 boss 在同一幕里出现两次"——整幕模组会把同一个 boss
            // 注册进多层池，所以只按层去重是不够的）。
            var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool Pickable(EncounterModel? e) =>
                e?.Id?.Entry is { } id && !defeated.Contains(id) && !chosen.Contains(id);

            /// 跨层汇总的"没打过且还没被选中"的池（某层用尽时的兜底）。
            List<EncounterModel> GlobalUnvisited() =>
                pools.SelectMany(p => p)
                     .Where(Pickable)
                     .GroupBy(e => e!.Id.Entry, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First())
                     .ToList();

            // ★ 2026-09-23 修（用户："之前打过的 BOSS 在第五幕重新出现了"）：
            //   旧实现的兜底是"该层全池随机"，等于**把打过的 boss 又抽一遍**。
            //   现在的优先序：本层没打过 → **全局没打过**（跨层兜底）→ 全部打过才允许重复（并打 Warn）。
            List<EncounterModel> Unvisited(int layer)
            {
                var pool = layer - 1 < pools.Count ? pools[layer - 1] : new List<EncounterModel>();
                var list = pool.Where(Pickable).ToList();
                if (list.Count > 0) return list;

                var global = GlobalUnvisited();
                if (global.Count > 0)
                {
                    MainFile.Logger.Warn(
                        $"[Act5] 第 {layer} 层没打过的 boss 已用尽（该层池 {pool.Count} 个）" +
                        $"⇒ 改从**全局没打过**的池里抽（{global.Count} 个），避免重复已打过的 BOSS");
                    return global;
                }

                MainFile.Logger.Warn(
                    $"[Act5] 本局所有层的 boss 都已经打过了 ⇒ 第 {layer} 层只能回退成重复抽取" +
                    "（已尽量避开本幕已选中的那几个）");
                return pool.Where(e => e?.Id?.Entry is { } id && !chosen.Contains(id)).ToList();
            }

            EncounterModel? PickFrom(List<EncounterModel> list)
            {
                if (list.Count == 0) return null;

                var picked = list[rng.NextInt(0, list.Count)];
                if (picked?.Id?.Entry is { } id) chosen.Add(id);   // 记入本幕已选，防止幕内重复
                return picked;
            }

            if (!extreme)
            {
                // ── 考验：一层随机 + 二层随机，最终BOSS 优先"第 3 层没打过的" ──
                _hearthsBetween = false;

                // ★ 2026-09-23 修：旧写法 `_finale = Act3FinalBoss(state)` 直接取
                //   "第 3 层当前分配的那个 boss" —— 而玩家在第 3 幕**已经亲手打过它**，
                //   于是最终 BOSS 必然重复（实测：第 3 幕 GLORY 的 boss = DOORMAKER_BOSS，
                //   第 5 幕的最终 BOSS 又抽到 DOORMAKER_BOSS）。
                //   现在与极限档同口径：**先从"第 3 层没打过"的池里抽**（该层空了会自动改用全局没打过的池），
                //   只有"本局所有 boss 都打过了"才退回 <see cref="Act3FinalBoss" />。
                var finalePool = Unvisited(3);
                _finale = PickFrom(finalePool) ?? Act3FinalBoss(state);

                Add(PickFrom(Unvisited(1)), 1);
                Add(PickFrom(Unvisited(2)), 2);

                LogPlan(extreme);
                return;
            }

            // ── 极限：一~二~三 循环取没打过的，三层那个压轴 ──
            _hearthsBetween = true;

            var layers = new[]
            {
                Shuffled(Unvisited(1), rng),
                Shuffled(Unvisited(2), rng),
                Shuffled(Unvisited(3), rng)
            };

            // 三层必须压轴：先从"没打过的三层"里抽一个当最终BOSS（抽不到就用 act3 当前分配的那个）
            var layer3 = layers[2];
            if (layer3.Count > 0)
            {
                _finale = PickFrom(layer3);
                layer3.Remove(_finale!);
            }
            else
            {
                _finale = Act3FinalBoss(state);
            }

            // 循环 1→2→3→1→2→3…（某层空了就跳过它）
            //
            // ⚠️ 2026-09-23：这一圈改成**有硬上限**的循环 + 幕内去重。原写法
            //   `for (i=0; Disguised.Count < Max; i++) { if (cursor 越界) continue; ... }`
            //   在"所有层都抽完但 Disguised 还没到上限"时靠 `anyLeft` 的 break 收工 ——
            //   一旦我们在里面加了"跳过已选过的"分支，continue 就可能**永远转下去**（卡死主线程）。
            //   这里给出总步数上限，任何异常输入都退化成"少放几场"，绝不进死循环。
            var cursor = new int[3];
            var totalCandidates = layers[0].Count + layers[1].Count + layers[2].Count;
            var maxSteps = (totalCandidates + 1) * 3 + 6;

            for (var step = 0; step < maxSteps && Disguised.Count < MaxDisguised; step++)
            {
                var layer = step % 3;
                if (cursor[layer] >= layers[layer].Count) continue;

                var candidate = layers[layer][cursor[layer]++];
                if (candidate == null) continue;      // 池里理论不该有 null；有就跳过，别把 null 塞进名单

                // 幕内去重：整幕模组会把同一个 boss 注册进多层池 ⇒ 只按层去重是不够的
                if (candidate.Id?.Entry is { } cid && !chosen.Add(cid)) continue;

                Disguised.Add(candidate);

                // 全空 → 收工
                var anyLeft = false;
                for (var k = 0; k < 3; k++)
                    if (cursor[k] < layers[k].Count) { anyLeft = true; break; }
                if (!anyLeft) break;
            }

            var total = 0;
            for (var k = 0; k < 3; k++) total += layers[k].Count;
            if (total > Disguised.Count + 1)
            {
                MainFile.Logger.Warn(
                    $"[Act5] 极限档：没打过的 boss 共 {total} 个，但本层最多放 {MaxDisguised} 场，" +
                    $"多余 {total - Disguised.Count - 1} 个丢弃");
            }

            LogPlan(extreme);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act5] 编排 BOSS 失败: {ex}");
        }
    }

    private static void Add(EncounterModel? encounter, int layer)
    {
        if (encounter == null)
        {
            MainFile.Logger.Warn($"[Act5] 第 {layer} 层抽不到任何 boss");
            return;
        }

        Disguised.Add(encounter);
    }

    private static List<EncounterModel> Shuffled(List<EncounterModel> source, Rng rng)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }

    private static void LogPlan(bool extreme)
    {
        MainFile.DebugLog(
            $"[Act5] BOSS 编排（{(extreme ? "极限" : "考验")}）: " +
            $"伪装={string.Join(" → ", Disguised.Select(e => e?.Id.Entry ?? "<空>"))} " +
            $"| 火堆间隔={_hearthsBetween} | 最终BOSS='{_finale?.Id.Entry ?? "<空>"}'");
    }

    /// <summary>按 id 从游戏自己的 encounter 池里取**原型（canonical）**（大小写不敏感）。</summary>
    public static EncounterModel? FindEncounter(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        try
        {
            foreach (var encounter in ModelDb.AllEncounters)
            {
                if (encounter?.Id?.Entry is { } entry
                    && string.Equals(entry, id, StringComparison.OrdinalIgnoreCase))
                    return encounter;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Act5] 按 id 查找 encounter '{id}' 失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>最终 BOSS 兜底 = **第 3 层（ACT3）当前分配的那个**（不硬编码 boss 名）。</summary>
    public static EncounterModel? Act3FinalBoss(RunState? state)
    {
        try
        {
            const int act3Index = 2;
            if (state?.Acts != null && state.Acts.Count > act3Index)
            {
                var act3Boss = state.Acts[act3Index]?.BossEncounter;
                if (act3Boss != null) return act3Boss;
            }

            MainFile.Logger.Warn("[Act5] 第 3 层的 BossEncounter 取不到，退回第 3 层 boss 池的第一个");
            return RunProgress.CollectFromLayer(3, a => a.AllBossEncounters).FirstOrDefault();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act5] 取第 3 层最终 BOSS 失败: {ex}");
            return null;
        }
    }
}
