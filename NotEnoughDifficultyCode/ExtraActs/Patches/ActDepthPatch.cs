using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT4 深度求解 —— **先数原版地图自己的战斗房/精英房**，再按用户规则加深或缩浅。
///
/// ## 用户规则
/// <code>
///   ① 数出当前地图所有可作精英的战斗位（小怪房 + 精英房，排除 BOSS）
///   ② 待打精英数 &gt; 地图精英房数 → 扩充深度：
///        新增行的问号/商店 → 火堆
///        新增行里多余的精英格 → 商店或问号
///   ③ 待打精英数 &lt; 地图精英房数 → 减少深度，直到
///        「待打精英数 ≥ 缩小后地图的精英房数」
///        缩完若变成"大于" → 转 ② 的逻辑
/// </code>
///
/// ## ⚠️ 为什么必须"数原版地图"（踩过坑）
/// 早期版本只读"待打精英数"，然后盲目往地图上补精英 → **模组一多就崩**：
/// 待打精英是所有模组精英池的并集（实测某局该层池 29 个），
/// 于是地图被塞成"一路全是精英"（日志：59 Monster→Elite）。
/// 正确做法是先知道**这张地图自己有多少战斗位/精英房**，以它为基准调整。
///
/// ## 怎么数（不卡死、不污染真图）
/// 房间数固定为原版基准时，原版 <c>StandardActMap</c> 会产出几个精英房？
/// 答案是造一张<b>临时地图</b>数一数 —— 与真图同源同结构，但：
/// <list type="bullet">
///   <item>rng 名字换成 <c>act{n}_map_probe</c> → 与真图的随机流**完全隔离**</item>
///   <item>不进 <c>RunState</c>、不注册 UI，用完即弃</item>
///   <item><b>整个进程只测一次</b>并缓存（同一层同一种子结果恒定）</item>
///   <item>房间数覆盖靠一个**只装一次**的静态 Harmony patch，进出只改一个字段</item>
/// </list>
/// </summary>
[HarmonyPatch(typeof(ActModel), nameof(ActModel.GetNumberOfRooms))]
public static class ActDepthPatch
{
    /// <summary>
    ///     房间数上限 = 跟着名单长度走（**不写死**）：
    /// 装了大内容模组（精英更多）时，地图必须够长才放得下每个精英，
    /// 但仍设一个天花板，避免把地图拉成离谱的长条。
    /// </summary>
    private static int MaxRoomsFor(int target) => Math.Clamp(target + 8, 20, 32);

    /// <summary>逼近迭代步数上限（保底出口，防止任何意外死循环）。</summary>
    private const int MaxSteps = 24;

    /// <summary>测量缓存：key = 种子|深度, value = 该深度下的精英房数（同一深度只造一次临时图）。</summary>
    private static readonly Dictionary<string, ActDepthPatch.MapMeasurement> _measured =
        new(StringComparer.Ordinal);

    internal static void Reset() => _measured.Clear();

    [HarmonyPriority(Priority.Low)]
    [HarmonyPostfix]
    public static void Postfix(ActModel __instance, bool isMultiplayer, ref int __result)
    {
        if (!PatchScope.IsEnabled) return;

        // ★ 只对 act4 生效 —— 其它层（含别的模组加的 act）一律不动
        if (__instance is not Act4Model) return;

        // ★ 重入闸：预演建图会再次触发本 postfix → 再预演…… 缺了这道闸会递归污染，
        //   而且会把测量结果搞成 0/0（踩过这个坑）
        if (Act4ProbeMap.InProbe) return;

        try
        {
            var state = RunStateAccessor.GetCurrentState();
            if (state == null) return;

            var target = Act4ElitePlan.TargetCount;   // 全部基础精英（当前 = 12），与"打没打过"无关
            var baseRooms = Math.Max(Act4Model.MinAct4Rooms, Act4Model.BaseNumberOfRoomsForDepth);

            // ① 先按原版基准预演一次（种子预测器）
            var m0 = MeasureCached(state, baseRooms);

            // ② 按用户算法逐轮逼近正确深度（规则转变 = 找到深度）
            var rooms = SolveRooms(state, baseRooms, m0.EliteRooms, target);
            var final = MeasureCached(state, rooms);

            __result = isMultiplayer ? Math.Max(rooms - 1, Act4Model.MinAct4Rooms - 1) : rooms;

            MainFile.DebugLog(
                $"[ActDepth] act4: 基准 {baseRooms} rooms → 战斗位 {m0.CombatRooms}/精英房 {m0.EliteRooms} | " +
                $"目标精英 {target} → 深度 {__result} rooms（该深度预计精英房 {final.EliteRooms}）" +
                $"（多人={isMultiplayer}）");
        }
        catch (Exception ex)
        {
            // 失败不改原值（保持原版深度），绝不因为本 patch 让游戏卡住
            MainFile.Logger.Error($"[ActDepth] 计算失败，保持原值 {__result}: {ex}");
        }
    }

    /// <summary>
    ///     **从小到大逐格找"战斗房数量 ≥ 目标精英数"的最浅深度**。
    ///
    /// ## 用户原话
    /// <i>"不要乱二分啊，又多了一对小怪，就按照之前的算法来"</i>
    /// <i>"其实不用管精英的呀 / 你精英和小怪没区别，全部当战斗房间来算就行"</i>
    /// <i>"应该是缩小战斗房到 12 个啊，深度拉低！"</i>
    ///
    /// ## 目标数 = 全部精英数（12），不是"没打过的精英数"
    /// 见 <see cref="Act4ElitePlan" />：act4 = 12 个精英各打一遍。
    ///
    /// ## 为什么不用二分（踩过的坑）
    /// 二分的前提是"战斗房数随深度**单调**"。但原版 <c>AssignPointTypes()</c> 有随机分配，
    /// 深度一变布局会重排 —— 实测出现深度 13 与 20 的战斗位从 35 跳到 46 这种非单调情况，
    /// 二分于是收敛到错误的深度。**逐格扫最稳**（和用户给的"差一个精英就挪一格"同源）。
    ///
    /// ## 算法
    /// <code>
    ///   从最低深度 floor 开始，一格格加深（每格都预演一次）：
    ///     深度 8  → 战斗房 20  （≥ 12 ★ 收敛，就用 8）
    ///   若一直不够，到 MaxRooms 为止（有上限保护）
    /// </code>
    /// 只在"战斗房 ≥ 目标"的**第一格**停下 —— 那是最浅的、够用的深度（深度越低地图越短）。
    /// </summary>
    private static int SolveRooms(RunState state, int baseRooms, int baseElites, int target)
    {
        if (target <= 0) return Act4Model.MinAct4Rooms;   // 没有精英可打 → 缩到最小

        var floor = Act4Model.MinAct4Rooms;
        var maxRooms = MaxRoomsFor(target);
        var depth = floor;
        var combat = MeasureCached(state, depth).CombatRooms;
        var steps = 0;

        // 逐格加深，直到战斗房够（或到上限）
        while (combat < target && depth < maxRooms && steps++ < MaxSteps)
        {
            depth++;
            combat = MeasureCached(state, depth).CombatRooms;
        }

        if (combat < target)
        {
            MainFile.Logger.Warn(
                $"[ActDepth] 到深度上限 {depth} 战斗房只有 {combat}（目标 {target}）——" +
                "按该深度继续，装不下的精英这次打不到（日志里会点名）");
        }
        else
        {
            MainFile.DebugLog(
                $"[ActDepth] 收敛: 深度 {depth} rooms → 战斗房 {combat} ≥ 目标 {target}（逐格 {steps} 轮）");
        }

        return Math.Clamp(depth, floor, maxRooms);
    }

    /// <summary>带缓存的预演：同一 (种子, 深度) 只造一次临时图。</summary>
    private static MapMeasurement MeasureCached(RunState state, int rooms)
    {
        var key = $"{state.Rng?.StringSeed}|{rooms}";
        if (_measured.TryGetValue(key, out var m)) return m;

        m = Act4ProbeMap.Measure(state, rooms);
        _measured[key] = m;
        return m;
    }

    /// <summary>
    ///     ⚠️ 时机约束：**不要**在 <c>ActModel.GetNumberOfRooms</c> / 建图过程里跑"数地图"的算法。
    ///     本方法由 <c>StandardActMap</c> 的构造函数调用，那一刻**地图还没有生成**（<c>state.Map</c> 是 null），
    ///     数不到战斗位/精英房 —— 早期版本在这里造临时地图来数，结果测量恒为 0/0，
    ///     求解一路加到触顶，把地图拉成 20 行长图（用户实测反馈）。
    ///     真正能数地图的时机是 <c>RunManager.GenerateMap</c> 之后，见 <see cref="Act5MapPatch" />。
    /// </summary>

    // ============================================================
    // 测量：原版地图在给定房间数下有多少战斗位 / 精英房
    // ============================================================

    internal readonly record struct MapMeasurement(int CombatRooms, int EliteRooms);

    /// <summary>
    ///     数**当前真地图**上的战斗位与精英房（排除 BOSS/起点/不可改位）。
    ///
    /// ⚠️ 早期版本造临时 <c>StandardActMap</c> 来数，结果恒为 0/0
    /// （临时环境下构造不出可改节点），把深度求解带偏到触顶。
    /// 现在直接数真地图 —— 准确、零开销、无风险。
    /// </summary>
    internal static MapMeasurement MeasureRealMap(RunState state)
    {
        var combat = 0;
        var elites = 0;

        try
        {
            var map = state.Map;
            if (map == null) return default;

            foreach (var p in map.GetAllMapPoints())
            {
                if (p == null || !p.CanBeModified) continue;

                if (p.PointType == MapPointType.Elite) { elites++; combat++; }
                else if (p.PointType is MapPointType.Monster or MapPointType.Unassigned) combat++;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[ActDepth] 数真地图失败: {ex.Message}");
        }

        return new MapMeasurement(combat, elites);
    }
}
