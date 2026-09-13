using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace NotEnoughDifficulty.NotEnoughDifficultyCode;

/// <summary>
///     ACT4 的「种子预测器」—— **用同一个种子预演建图，数出会有几个精英房**。
///
/// ## 为什么这不算"离谱的穷举"
/// 用户提醒得对：如果真去做通用种子预测（反推整个游戏数据），那是天方夜谭。
/// 但这里**不需要反推** —— 我们直接调游戏自己的建图逻辑，只是换一条随机流：
/// <code>
///   真图:   new Rng(runState.Rng.Seed, $"act{actIndex+1}_map")
///   预演:   new Rng(runState.Rng.Seed, $"act{actIndex+1}_map_probe{rooms}")
/// </code>
/// 只是跑一遍 <c>StandardActMap</c> 的构造（用户实测约 1 秒，可接受），
/// 数完就丢。既不需要读懂原版算法，也不用担心算法改版。
///
/// ## 三条安全线（早期版本就是因为缺这些把游戏卡死 / 测量恒为 0）
/// <list type="number">
///   <item><b>只装一次 patch</b>：房间数覆盖靠静态 patch + 一个静态字段，
///         不在每次预演时 patch/unpatch（那是卡死的直接成因）</item>
///   <item><b>重入闸 <see cref="InProbe" /></b>：预演会再次触发
///         <c>ActModel.GetNumberOfRooms</c> → 进而触发 <see cref="ActDepthPatch" /> 的求解器
///         → 又去预演…… 没有这道闸会递归污染，**测量结果会变成 0/0**（踩过这个坑）</item>
///   <item><b>尺寸/次数校验</b>：房间数超范围直接放弃；同一深度只预演一次（缓存）</item>
/// </list>
/// </summary>
internal static class Act4ProbeMap
{
    private static int _roomsOverride = -1;
    private static bool _installed;
    private static readonly object Gate = new();

    /// <summary>是否正在预演，供 <see cref="ActDepthPatch" /> 重入保护。</summary>
    internal static bool InProbe { get; private set; }

    /// <summary>预演：深度为 <paramref name="rooms" /> 时会产出多少战斗位/精英房。</summary>
    internal static ActDepthPatch.MapMeasurement Measure(RunState state, int rooms)
    {
        // ★ 用 state.Acts 里的 act4 实例，而不是 state.Act
        //   （建图期 state.Act 可能还没切到 act4 —— 那会让预演造出一张不属于 act4 的地图）
        var act4 = state?.Acts?.OfType<Act4Model>().FirstOrDefault();
        if (act4 == null) return default;

        // 尺寸安全阀：太小会让原版 AssignPointTypes 数组越界，太大没必要
        if (rooms < 8 || rooms > 30) return default;

        lock (Gate)
        {
            try
            {
                EnsurePatched();
                _roomsOverride = rooms;
                InProbe = true;

                var rng = new Rng(state!.Rng.Seed, $"act4_map_probe{rooms}");

                var probe = new StandardActMap(
                    rng,
                    act4,
                    isMultiplayer: state.Players.Count > 1,
                    shouldReplaceTreasureWithElites: false,
                    hasSecondBoss: act4.HasSecondBoss,
                    mapPointTypeCountsOverride: null,
                    enablePruning: true);

                var combat = 0;
                var elites = 0;
                var total = 0;
                foreach (var p in probe.GetAllMapPoints())
                {
                    if (p == null) continue;
                    total++;

                    if (p.PointType == MapPointType.Elite) { elites++; combat++; }
                    else if (p.PointType is MapPointType.Monster or MapPointType.Unassigned) combat++;
                }

                MainFile.DebugLog(
                    $"[Act4Probe] 预演 {rooms} rooms: 网格点数={total}, 战斗位={combat}, 精英房={elites}");

                return new ActDepthPatch.MapMeasurement(combat, elites);
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Act4Probe] 预演 {rooms} rooms 失败: {ex}");
                return default;
            }
            finally
            {
                InProbe = false;
                _roomsOverride = -1;
            }
        }
    }

    private static void EnsurePatched()
    {
        if (_installed) return;

        var target = AccessTools.Method(typeof(ActModel), nameof(ActModel.GetNumberOfRooms));
        if (target == null)
        {
            MainFile.Logger.Warn("[Act4Probe] 找不到 ActModel.GetNumberOfRooms，预演不可用");
            return;
        }

        var harmony = new Harmony("NotEnoughDifficulty.Act4Probe");
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(Act4ProbeMap), nameof(RoomsPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(Act4ProbeMap), nameof(RoomsPostfix))));

        _installed = true;
        MainFile.DebugLog("[Act4Probe] 房间数覆盖 patch 已装载（只装一次）");
    }

    private static bool RoomsPrefix(ref int __result)
    {
        var v = _roomsOverride;
        if (v < 0) return true;
        __result = v;
        return false;
    }

    private static void RoomsPostfix(ref int __result)
    {
        var v = _roomsOverride;
        if (v >= 0) __result = v;
    }
}
