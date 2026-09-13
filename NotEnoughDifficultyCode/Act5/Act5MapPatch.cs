using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     act4 火堆溢出（需求2）。
///
/// ## act5 不在这里了
/// act5 现在只有三个房间：**先古之名 → 伪BOSS → 最终BOSS**。
/// 地图由 <c>Act5Model.CustomCreateMap</c> 提供，两个 BOSS 槽位由
/// <see cref="ActBlueprint" /> 在**黑屏窗口内**一次定稿 —— 都不需要本 patch 插手。
///
/// ## act4
/// act4 的战斗节点已经是 <c>Elite</c>（由本文件的 <c>RebalanceAct4Map</c> 保证），
/// 深度由 <c>Act4Model.GetNumberOfRooms</c> 按未打精英数决定。
///
/// 时机：<c>RunManager.GenerateMap</c> postfix（地图已生成完，节点/连边齐全）。
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateMap))]
public static class Act5MapPatch
{
    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void RebuildMap(RunManager __instance)
    {
        if (!PatchScope.IsEnabled) return;

        PatchScope.Run(nameof(Act5MapPatch), () =>
        {
            var state = RunStateAccessor.GetState(__instance);
            if (state?.Map == null) return;

            if (state.Act is Act4Model) RebalanceAct4Map(state);
        });
    }

    /// <summary>
    ///     ACT4：把战斗房数量对齐到**全部精英数**（当前 = 12），并把精英"落位"到每个战斗房上。
    ///
    /// ## 用户口径（逐字）
    /// <i>"其实不用管精英的呀 / 你精英和小怪没区别，全部当战斗房间来算就行"</i>
    /// <i>"应该是缩小战斗房到 12 个啊，深度拉低！"</i>
    /// <i>"我的意思是，地图里面分配的精英是确定的，不是随机的！而且就９个精英有点离谱了"</i>
    ///
    /// 所以这里**不做**精英图标促销/降级，只做三件事：
    /// <list type="number">
    ///   <item>数出地图上的**战斗房总数**（精英房 + 小怪房，排除 BOSS/起点）</item>
    ///   <item>深度已由 <see cref="ActDepthPatch" /> 逐格求解到"战斗房 ≥ 全部精英数"</item>
    ///   <item>兜底：① 富余战斗位 → 火堆；② 扩深行的问号/商店 → 火堆；③ 不够则问号 → 精英</item>
    /// </list>
    /// 收尾时 <see cref="Act4ElitePlan.Invalidate" /> —— 节点类型刚变过，落位表必须按新战斗房重建。
    ///
    /// 注意**不看** <c>CanBeModified</c>：原版会把第 0/1 行硬编码成固定类型，
    /// 那些也是战斗房，必须一起算进来。
    /// </summary>
    private static void RebalanceAct4Map(RunState state)
    {
        var map = state.Map!;

        var target = Act4ElitePlan.TargetCount;    // 全部基础精英（12），与"打没打过"无关
        if (target <= 0)
        {
            MainFile.Logger.Warn("[Act4] 精英名单为空（拿不到基础精英池），不做地图校正");
            return;
        }

        var depth = ComputeDepths(map);
        if (depth.Count == 0)
        {
            MainFile.Logger.Warn("[Act4] 无法计算节点深度，跳过地图校正");
            return;
        }

        var baselineDepth = Act4Model.BaseNumberOfRoomsForDepth - 1;

        try
        {
            // ── ① 数战斗房（不看 CanBeModified）与扩深行的问号/商店 ──
            //     顺序与 Act4ElitePlan 的落位顺序**同一套**（离起点由近到远）——
            //     落位表就是按这个顺序把名单一个个钉上去的。
            var combatPoints = Act4ElitePlan.OrderedCombatPoints(map);
            var extraNonCombat = new List<MapPoint>();

            foreach (var p in map.GetAllMapPoints())
            {
                if (p == null) continue;
                if (p.PointType is not (MapPointType.Unknown or MapPointType.Shop)) continue;

                if (depth.TryGetValue(p, out var d) && d > baselineDepth)
                    extraNonCombat.Add(p);
            }

            var mapCombat = combatPoints.Count;

            // ── ② 扩深行的问号/商店 → 火堆（规则 ②）──
            var toRest = 0;
            foreach (var p in extraNonCombat)
            {
                if (p.PointType is MapPointType.Unknown or MapPointType.Shop)
                {
                    p.PointType = MapPointType.RestSite;
                    toRest++;
                }
            }

            // ── ③ 富余的战斗位 → 火堆（真正把战斗房减到"名单长度"）──
            var surplus = mapCombat - target;
            var removed = 0;
            if (surplus > 0)
            {
                // 从地图**最深**处开始退（那些是扩深挤出来的），也要跳过不可改位
                var ordered = combatPoints
                    .Where(p => p.CanBeModified)
                    .OrderByDescending(p => depth.TryGetValue(p, out var dd) ? dd : 0)
                    .ThenByDescending(p => p.coord.col)
                    .ToList();

                foreach (var p in ordered)
                {
                    if (surplus <= 0) break;
                    p.PointType = MapPointType.RestSite;
                    removed++;
                    surplus--;
                }

                if (surplus > 0)
                    MainFile.Logger.Warn(
                        $"[Act4] 还有 {surplus} 个战斗位退不掉（不可修改）—— 实际战斗房会多于名单");
            }

            // ── ④ 战斗房的图标统一成精英 ──
            //
            // 用户要求："小怪直接被替换成精英的"。
            // 逻辑上也自洽：本层的每个战斗房都被落位表指定了一个精英（Act4ElitePlan），
            // 所以战斗房的**图标**也该是精英。
            //
            // ⚠️ 不看 CanBeModified：原版把第 0/1 行硬编码成固定类型（其中第一行是小怪房），
            //    那些同样是战斗房，图标也要统一。
            var iconChanged = 0;
            foreach (var p in map.GetAllMapPoints())
            {
                if (p == null) continue;
                if (p.PointType is MapPointType.Monster or MapPointType.Unassigned)
                {
                    p.PointType = MapPointType.Elite;
                    iconChanged++;
                }
            }

            var finalCombat = 0;
            var finalElite = 0;
            foreach (var p in map.GetAllMapPoints())
            {
                if (p?.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Unassigned)
                    finalCombat++;
                if (p?.PointType == MapPointType.Elite) finalElite++;
            }

            // ── ⑤ 还不够？把**问号**直接补成精英 ──
            //    实测（用户："问号太多了 / 10 精英怎么还有 9 层"）：`待打精英=12 | 地图战斗房=7`，
            //    而"扩深行/富余位"两条都是 0 ⇒ 原版建图给 act4 的战斗位本来就不够、问号本来偏多，
            //    前三条校正无从下手。这里按缺额把问号转成精英：战斗房补到"名单长度"，问号自然变少。
            var questionToElite = 0;
            var shortfall = target - mapCombat - removed;
            if (shortfall > 0)
            {
                foreach (var p in map.GetAllMapPoints()
                             .Where(x => x != null && x.PointType == MapPointType.Unknown)
                             .OrderByDescending(x => depth.TryGetValue(x!, out var d) ? d : 0)
                             .ToList())
                {
                    if (questionToElite >= shortfall) break;
                    p!.PointType = MapPointType.Elite;
                    p.CanBeModified = false;
                    questionToElite++;
                }
            }

            // ★ 节点类型刚改过 ⇒ 落位表必须按新的战斗房集合重建（否则表里还留着刚变成火堆的点）
            Act4ElitePlan.Invalidate();

            MainFile.DebugLog(
                $"[Act4] 地图校正: 目标精英(名单)={target} | 地图战斗房={mapCombat} | " +
                $"扩深行问号/商店→火堆={toRest} | 富余战斗位→火堆={removed} | " +
                $"问号→精英={questionToElite}（缺额 {shortfall}） | 小怪图标→精英={iconChanged} | " +
                $"最终：战斗房={finalCombat}（其中精英图标 {finalElite}）");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act4] 地图校正失败: {ex}");
        }
    }

    /// <summary>
    ///     给每个节点算"离起点的最短距离"（BFS 沿 <c>Children</c> 向后走）。
    ///     起点距离 0，每下一行 +1，所以距离 == 行号（原版地图每行只能从前一行到达）。
    ///     <see cref="Act4ElitePlan" /> 也用它给战斗房排序（离起点由近到远 = 玩家实际遇到顺序）。
    /// </summary>
    internal static Dictionary<MapPoint, int> ComputeDepths(ActMap map)
    {
        var result = new Dictionary<MapPoint, int>();
        try
        {
            var start = map.StartingMapPoint;
            if (start == null) return result;

            var queue = new Queue<MapPoint>();
            result[start] = 0;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                var d = result[cur];
                var children = cur.Children;
                if (children == null) continue;

                foreach (var child in children)
                {
                    if (child == null) continue;
                    if (result.TryGetValue(child, out var known) && known <= d + 1) continue;
                    result[child] = d + 1;
                    queue.Enqueue(child);
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Act4] 计算节点深度失败: {ex}");
        }

        return result;
    }
}
