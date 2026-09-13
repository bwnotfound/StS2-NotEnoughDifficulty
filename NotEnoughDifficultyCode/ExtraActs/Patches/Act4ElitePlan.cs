using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT4 的**精英落位表**：把"该层的全部精英"逐个**确定地**钉在每一个战斗房上。
///
/// ## 用户口径（逐字）
/// <code>
/// 精英还是好少啊，你确定算法对吗？而且为什么会有精英重复啊？
/// 我的意思是，地图里面分配的精英是确定的，不是随机的！而且就９个精英有点离谱了
/// </code>
///
/// ## 事实（IL 实证，决定了"12"这个数）
/// base game 一共 **4 个 act、每个 3 个精英 = 12 个精英**：
/// <code>
///   层1 Overgrowth   : BYGONE_EFFIGY / BYRDONIS / PHROG_PARASITE
///   层1 Underdocks   : PHANTASMAL_GARDENERS / SKULKING_COLONY / TERROR_EEL      ← 同层第二个变体！
///   层2 Hive         : DECIMILLIPEDE / ENTOMANCER / INFESTED_PRISMS
///   层3 Glory        : KNIGHTS / MECHA_KNIGHT / SOUL_NEXUS
/// </code>
/// 而地图的战斗位正好 12 个左右 ⇒ **act4 就是把 12 个精英各打一遍**，
/// 与"玩家打没打过"无关（旧实现按"未打精英"过滤 → 只打了 9 个，用户："９个精英有点离谱"）。
///
/// ## ⚠️ 一个数字都不写死（模组兼容）
/// <list type="bullet">
///   <item>名单来自 <see cref="RunProgress.GetAllBaseElites" /> —— 走 <c>ModelDb.ActsByIndex</c> 按层聚合，
///         整幕类模组往同一层塞的 act 变体、以及它们自己的精英<b>自动进名单</b>；</item>
///   <item>目标战斗房数 = **名单长度**（不是常量）；深度上限也跟着名单长度走
///         （见 <c>ActDepthPatch.MaxRoomsFor</c>）；</item>
///   <item>战斗房集合 = 地图上实际是 Monster/Elite/Unassigned 的点（不按行号/列号写死）；</item>
///   <item>取怪只在"本层是 act4 且这个坐标有落位"时接管，其余一律 <c>return true</c> 交回原版
///         ⇒ 不会吞掉别的模组的取怪逻辑。</item>
/// </list>
///
/// ## 旧实现为什么会重复（两个 bug 叠在一起）
/// <code>
///   target = Math.Max(unvisited.Count, rooms.eliteEncounters.Count);
///   rooms.eliteEncounters.Add(unvisited[i % unvisited.Count]);   // ← 取模循环 ⇒ 池子里出现重复项
///   ...原版再从池子里 rng.NextItem() 抽 ⇒ 同一场精英可能被抽两次
/// </code>
/// 现在改成**先落位、后取怪**：地图一生成就把名单按"离起点由近到远"的顺序
/// 逐个钉到战斗房上（<see cref="ForCoord" />），战斗时按**当前坐标**查表返回，
/// 原版的随机抽取完全不参与 ⇒ 不重复、且每个节点打谁在进图那一刻就定了。
///
/// ## 为什么按坐标查表而不是按"第几场"
/// 按"第几场"数（<c>VisitedMapCoords</c> 计数）在**读档后**也对，但一旦玩家绕路/回看地图就会算错；
/// 坐标是身份，最稳：<c>RunState.CurrentMapCoord</c> 就是"正在进入的坐标"
/// （IL：<c>AddVisitedMapCoord</c> 先 append，再 <c>CreateRoom</c> → <c>PullNextEncounter</c>）。
/// </summary>
internal static class Act4ElitePlan
{
    /// <summary>当前落位表所属的地图实例（地图一换就重建）。</summary>
    private static ActMap? _plannedMap;

    /// <summary>坐标 → 该战斗房指定的精英。</summary>
    private static Dictionary<MapCoord, EncounterModel> _plan = new();

    /// <summary>
    ///     名单 = **全部基础精英**（层 1→3、层内按 act、act 内按池顺序），全局去重。
    ///     顺序确定 ⇒ 玩家每局遇到的顺序也确定（由下往上、先 act1 后 act2、act3）。
    /// </summary>
    internal static List<EncounterModel> Roster()
    {
        var result = new List<EncounterModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in RunProgress.GetAllBaseElites())
        {
            var id = e?.Id?.Entry;
            if (e == null || string.IsNullOrEmpty(id)) continue;
            if (seen.Add(id!)) result.Add(e);
        }

        return result;
    }

    /// <summary>目标战斗房数 = 名单长度（当前版本 = 12）。</summary>
    internal static int TargetCount => Roster().Count;

    /// <summary>地图重建/节点类型改动后必须调用（否则表还是旧地图的）。</summary>
    internal static void Invalidate()
    {
        _plannedMap = null;
        _plan = new Dictionary<MapCoord, EncounterModel>();
    }

    /// <summary>该战斗房（坐标）指定的精英；没有落位返回 null（调用方交回原版）。</summary>
    internal static EncounterModel? ForCoord(RunState? state, MapCoord coord)
    {
        var map = state?.Map;
        if (map == null) return null;

        EnsurePlan(map);
        return _plan.TryGetValue(coord, out var encounter) ? encounter : null;
    }

    /// <summary>
    ///     战斗房（精英/小怪/未定）按**离起点由近到远**排序 —— 这就是玩家实际会遇到的顺序：
    ///     先按 BFS 深度（= 行号）升序，同深度按列升序，保证两端一致、每局一致。
    /// </summary>
    internal static List<MapPoint> OrderedCombatPoints(ActMap map)
    {
        var depth = Act5MapPatch.ComputeDepths(map);

        return map.GetAllMapPoints()
            .Where(p => p != null && p.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Unassigned)
            .OrderBy(p => depth.TryGetValue(p!, out var d) ? d : int.MaxValue)
            .ThenBy(p => p!.coord.col)
            .ThenBy(p => p!.coord.row)
            .Select(p => p!)
            .ToList();
    }

    private static void EnsurePlan(ActMap map)
    {
        // 地图实例没变、且**战斗房集合也没变**时直接复用。
        // 战斗房数量对不上就重建 —— 这样即便别的模组在地图生成后又改了节点类型
        // （或者读档进了一个我们没参与生成的地图），落位表也能自愈，不会指到火堆/问号上。
        if (ReferenceEquals(_plannedMap, map) && _plan.Count > 0 &&
            _plan.Count == OrderedCombatPoints(map).Count)
            return;

        _plannedMap = map;
        _plan = Build(map);
    }

    private static Dictionary<MapCoord, EncounterModel> Build(ActMap map)
    {
        var result = new Dictionary<MapCoord, EncounterModel>();
        var roster = Roster();
        if (roster.Count == 0) return result;

        var points = OrderedCombatPoints(map);
        for (var i = 0; i < points.Count && i < roster.Count; i++)
            result[points[i].coord] = roster[i];

        MainFile.DebugLog(
            $"[Act4] 精英落位: {result.Count} 个战斗房 ← 名单 {roster.Count} 个 | " +
            $"由下往上 = {string.Join(" → ", roster.Take(result.Count).Select(e => e.Id.Entry))}");

        if (points.Count < roster.Count)
            MainFile.Logger.Warn(
                $"[Act4] 地图只有 {points.Count} 个战斗房 < 名单 {roster.Count} 个 —— " +
                $"{roster.Count - points.Count} 个精英这次打不到（深度求解没给够位置）");

        return result;
    }
}

/// <summary>
///     战斗房取怪：**按坐标查落位表**。这是 act4 唯一给怪的地方
///     （原版随机抽取被完全跳过 ⇒ 不会重复）。
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.PullNextEncounter))]
internal static class Act4ElitePlanPickPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.High)]
    private static bool Prefix(ActModel __instance, RoomType roomType, ref EncounterModel __result)
    {
        if (!PatchScope.IsEnabled) return true;
        if (__instance is not Act4Model) return true;
        if (roomType is not (RoomType.Elite or RoomType.Monster)) return true;

        var state = RunStateAccessor.GetCurrentState();
        if (state?.Act is not Act4Model) return true;
        if (state.CurrentMapCoord is not { } coord) return true;

        var planned = Act4ElitePlan.ForCoord(state, coord);
        if (planned == null) return true;      // 没落位就交回原版，别把问题掩盖成空房

        MainFile.DebugLog($"[Act4] 战斗房 ({coord.col},{coord.row}) → 指定精英 '{planned.Id.Entry}'");
        __result = planned;
        return false;
    }
}
