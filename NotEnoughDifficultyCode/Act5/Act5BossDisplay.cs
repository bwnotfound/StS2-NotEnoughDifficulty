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

            List<EncounterModel> Unvisited(int layer)
            {
                var pool = layer - 1 < pools.Count ? pools[layer - 1] : new List<EncounterModel>();
                var list = pool.Where(e => e?.Id?.Entry is { } id && !defeated.Contains(id)).ToList();

                if (list.Count == 0 && pool.Count > 0)
                {
                    MainFile.Logger.Warn(
                        $"[Act5] 第 {layer} 层的 boss 全都打过（{pool.Count} 个），回退成该层全池随机");
                    list = pool.ToList();
                }

                return list;
            }

            EncounterModel? PickFrom(List<EncounterModel> list) =>
                list.Count > 0 ? list[rng.NextInt(0, list.Count)] : null;

            if (!extreme)
            {
                // ── 考验：一层随机 + 二层随机，最终BOSS = 三层当前分配的那个 ──
                _hearthsBetween = false;
                Add(PickFrom(Unvisited(1)), 1);
                Add(PickFrom(Unvisited(2)), 2);
                _finale = Act3FinalBoss(state);

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
            var cursor = new int[3];
            for (var i = 0; Disguised.Count < MaxDisguised; i++)
            {
                var layer = i % 3;
                if (cursor[layer] >= layers[layer].Count) continue;

                Disguised.Add(layers[layer][cursor[layer]++]);

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
